using System.Collections.Concurrent;
using OpenTrafficDB.App.Pipeline;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Singleton background service that samples pipeline stats every 15 seconds
/// and retains the last hour (240 samples) so the admin dashboard can show
/// pre-populated charts on login.
///
/// Rates are computed as per-sample deltas (not rolling-window sums) to avoid
/// the "rate increases during a burst then stays high" artefact.
/// </summary>
public sealed class StatsHistoryService(SpeedPipeline pipeline, SpeedAggregator aggregator, ILogger<StatsHistoryService> logger) : BackgroundService
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
    private long _prevIngested  = -1;
    private long _prevProcessed = -1;

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

    private Task SampleAsync(CancellationToken ct)
    {
        // Exact counters from the in-memory pipeline — no database queries.
        var matchingQueue  = (int)Math.Min(int.MaxValue, pipeline.BufferedPoints);
        var aggregateQueue = aggregator.PendingSamples;
        var totalQueue     = matchingQueue + aggregateQueue;

        var ingested  = pipeline.IngestedTotal;
        var processed = pipeline.ProcessedTotal;

        long ingestDelta = 0, processedDelta = 0;
        if (_prevIngested >= 0)
        {
            ingestDelta    = ingested  - _prevIngested;
            processedDelta = processed - _prevProcessed;
        }
        _prevIngested  = ingested;
        _prevProcessed = processed;

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
        return Task.CompletedTask;
    }
}
