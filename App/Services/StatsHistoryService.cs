using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Singleton background service that samples pipeline stats every 15 seconds
/// and retains the last hour (240 samples) so the admin dashboard can show
/// pre-populated charts on login.
///
/// Rates are computed as per-sample deltas (not rolling-window sums) to avoid
/// the "rate increases during a burst then stays high" artefact.
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
    private const long SampleIntervalSeconds = 15;

    private readonly ConcurrentQueue<StatsSample> _samples = new();

    // State carried between samples for delta computation.
    private long _prevTotalProcessed = -1;
    private int  _prevTotalQueue     = -1;

    public IReadOnlyList<StatsSample> GetHistory() => _samples.ToArray();

    /// <summary>The most recent sample, or null before the first sample completes.</summary>
    public StatsSample? Latest { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SampleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "StatsHistoryService: failed to sample stats"); }

            await Task.Delay(TimeSpan.FromSeconds(SampleIntervalSeconds), stoppingToken);
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var matchingQueue  = await db.SpeedObservations.CountAsync(o => o.OsmWayId == null && !o.IsAggregated, ct);
        var aggregateQueue = await db.SpeedObservations.CountAsync(o => o.OsmWayId != null && !o.IsAggregated, ct);
        var totalQueue     = matchingQueue + aggregateQueue;

        // Cumulative total of all observations ever aggregated.
        // AggregationMetrics rows are pruned after 24 h; if the sum drops (pruning), treat delta as 0.
        var totalProcessed = await db.AggregationMetrics.SumAsync(m => (long)m.ObservationCount, ct);

        long processedDelta = 0;
        long ingestDelta    = 0;

        if (_prevTotalProcessed >= 0)
        {
            // Guard against apparent decrease caused by row pruning.
            processedDelta = Math.Max(0, totalProcessed - _prevTotalProcessed);

            // ingest = observations processed this interval + net queue growth
            // (negative queue growth means we drained faster than we ingested)
            ingestDelta = Math.Max(0, processedDelta + (totalQueue - _prevTotalQueue));
        }

        _prevTotalProcessed = totalProcessed;
        _prevTotalQueue     = totalQueue;

        // Convert 15-second delta to per-minute rate.
        var processedPerMin = processedDelta * (60 / SampleIntervalSeconds);
        var ingestPerMin    = ingestDelta    * (60 / SampleIntervalSeconds);

        var sample = new StatsSample(
            Timestamp:       DateTime.UtcNow,
            TotalQueue:      totalQueue,
            MatchingQueue:   matchingQueue,
            AggregateQueue:  aggregateQueue,
            IngestPerMin:    ingestPerMin,
            ProcessedPerMin: processedPerMin
        );

        Latest = sample;
        _samples.Enqueue(sample);
        while (_samples.Count > MaxSamples)
            _samples.TryDequeue(out _);
    }
}
