using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Singleton background service that samples pipeline stats every 15 seconds
/// and retains the last hour (240 samples) so the admin dashboard can show
/// pre-populated charts on login.
/// </summary>
public sealed class StatsHistoryService(IServiceScopeFactory scopeFactory, ILogger<StatsHistoryService> logger) : BackgroundService
{
    public record StatsSample(
        DateTime Timestamp,
        int TotalQueue,
        int MatchingQueue,
        int AggregateQueue,
        long IngestPerMin,
        long ProcessedPerMin
    );

    private const int MaxSamples = 240; // 240 × 15 s = 1 hour
    private readonly ConcurrentQueue<StatsSample> _samples = new();

    public IReadOnlyList<StatsSample> GetHistory() => _samples.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger startup slightly so DB is ready.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "StatsHistoryService: failed to sample stats"); }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff5 = DateTime.UtcNow.AddMinutes(-5);

        var matchingQueue  = await db.SpeedObservations.CountAsync(o => o.OsmWayId == null && !o.IsAggregated, ct);
        var aggregateQueue = await db.SpeedObservations.CountAsync(o => o.OsmWayId != null && !o.IsAggregated, ct);
        var ingestLast5Min = await db.SpeedObservations.CountAsync(o => o.CreatedAt >= cutoff5, ct);
        var processedLast5Min = await db.AggregationMetrics
            .Where(m => m.ProcessedAt >= cutoff5)
            .SumAsync(m => (long)m.ObservationCount, ct);

        _samples.Enqueue(new StatsSample(
            Timestamp:      DateTime.UtcNow,
            TotalQueue:     matchingQueue + aggregateQueue,
            MatchingQueue:  matchingQueue,
            AggregateQueue: aggregateQueue,
            IngestPerMin:   ingestLast5Min / 5,
            ProcessedPerMin: processedLast5Min / 5
        ));

        // Trim to MaxSamples
        while (_samples.Count > MaxSamples)
            _samples.TryDequeue(out _);
    }
}
