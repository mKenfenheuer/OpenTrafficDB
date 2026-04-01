using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Coordinator that queries pending work, splits it evenly across all healthy
/// AggregateWorker instances, and dispatches non-overlapping job chunks.
///
/// Match jobs are partitioned by trip ID (trips never span workers).
/// Aggregate jobs are partitioned by OSM way ID so no two workers ever upsert
/// the same WaySpeedAggregate row — eliminating deadlocks entirely.
///
/// Config keys:
///   Coordinator:ChunkSize       — items per job chunk (default: 200)
///   Coordinator:IntervalSeconds — how often the coordinator scans for work (default: 30)
/// </summary>
public class AggregationCoordinatorService(
    IServiceScopeFactory scopeFactory,
    WorkerClient workerClient,
    IConfiguration config,
    ILogger<AggregationCoordinatorService> logger) : BackgroundService
{
    private TimeSpan Interval          => TimeSpan.FromSeconds(double.Parse(config["Coordinator:IntervalSeconds"] ?? "60"));
    private int      MapMatchingTripChunkSize         => int.Parse(config["Coordinator:MapMatchingTripChunkSize"] ?? "200");
    private int      AggregateWayIdChunkSize         => int.Parse(config["Coordinator:AggregateWayIdChunkSize"] ?? "200");
    private int      MatchConcurrency  => int.Parse(config["Coordinator:MatchConcurrencyPerWorker"] ?? "2");

    private static readonly Action<ILogger, int, int, int, Exception?> _logCycle =
        LoggerMessage.Define<int, int, int>(LogLevel.Information, default,
            "Cycle: {Unmatched} unmatched trips, {Unaggregated} unaggregated ways, {Workers} workers.");
    private static readonly Action<ILogger, int, string, int, Exception?> _logMatchChunkOk =
        LoggerMessage.Define<int, string, int>(LogLevel.Debug, default,
            "Match chunk ({Count} trips) on {Url}: {Matched} matched.");
    private static readonly Action<ILogger, int, string, Exception?> _logMatchChunkFail =
        LoggerMessage.Define<int, string>(LogLevel.Warning, default,
            "Match chunk ({Count} trips) on {Url} failed.");
    private static readonly Action<ILogger, int, string, int, Exception?> _logAggChunkOk =
        LoggerMessage.Define<int, string, int>(LogLevel.Debug, default,
            "Aggregate chunk ({Count} ways) on {Url}: {Processed} processed.");
    private static readonly Action<ILogger, int, string, Exception?> _logAggChunkFail =
        LoggerMessage.Define<int, string>(LogLevel.Warning, default,
            "Aggregate chunk ({Count} ways) on {Url} failed.");

    // Guards against cycle overlap within and across instances.
    // A full coordinator lock requires Redis in the App tier; this handles single-instance overlap.
    private int _cycleRunning = 0;

    // Tracks consecutive match failures per trip. Trips that fail MaxMatchRetries times
    // are skipped to avoid wasting OTR calls on unmatchable GPS traces.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _tripFailures = new();
    private int MaxMatchRetries => int.Parse(config["Coordinator:MaxMatchRetries"] ?? "3");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Aggregation coordinator started. Interval: {Interval}, AggregateWayIdChunkSize: {AggregateWayIdChunkSize}, MapMatchingTripChunkSize: {MapMatchingTripChunkSize}.",
            Interval, AggregateWayIdChunkSize, MapMatchingTripChunkSize);

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Interlocked.CompareExchange(ref _cycleRunning, 1, 0) == 0)
            {
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger.LogError(ex, "Coordinator cycle failed."); }
                finally
                {
                    Interlocked.Exchange(ref _cycleRunning, 0);
                }
            }
            else
            {
                logger.LogWarning("Coordinator cycle still running — skipping interval.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var workerUrls = workerClient.GetUrls("Aggregate");
        if (workerUrls is null || workerUrls.Length == 0)
        {
            logger.LogWarning("No Aggregate worker URLs configured (Workers:Aggregate:Urls) — skipping cycle.");
            return;
        }

        var unmatchedTrips   = await GetUnmatchedTripIdsAsync(ct);

        // Filter out trips that have repeatedly failed map matching.
        var maxRetries = MaxMatchRetries;
        var skipped = unmatchedTrips.RemoveAll(t => _tripFailures.TryGetValue(t, out var c) && c >= maxRetries);
        if (skipped > 0)
            logger.LogDebug("Skipping {Count} trips that failed map matching {Max}+ times.", skipped, maxRetries);


        var unaggregatedWays = await GetUnaggregatedWayIdsAsync(ct);
        // ── 1+2. Match and aggregate interleaved across workers ───────────────
        if (unmatchedTrips.Count > 0 || unaggregatedWays.Count > 0)
            await DispatchInterleavedAsync(unmatchedTrips, unaggregatedWays, workerUrls, ct);


        _logCycle(logger, unmatchedTrips.Count, unaggregatedWays.Count, workerUrls.Length, null);

        // ── 3. Finalize — needed when aggregation ran (initial or from match results) ──
        if (unaggregatedWays.Count > 0 || unmatchedTrips.Count > 0)
            await workerClient.SendFinalizeAsync(ct);
    }

    /// <summary>
    /// Dispatches match and aggregate chunks to workers via two independent channels.
    /// Each worker runs a match lane and an aggregate lane concurrently, so Valhalla I/O
    /// and DB aggregation overlap — neither waits on the other.
    ///
    /// The aggregate channel stays open while matching runs: each completed match job
    /// feeds its newly-matched way IDs straight into the aggregate channel so workers
    /// aggregate continuously instead of waiting for the next coordinator cycle.
    /// </summary>
    private async Task DispatchInterleavedAsync(
        List<string> trips, List<long> ways, string[] workerUrls, CancellationToken ct)
    {
        var matchChunks = Chunk(trips, MapMatchingTripChunkSize);
        var aggChunks   = Chunk(ways,  AggregateWayIdChunkSize);

        logger.LogInformation("Dispatching {M} match + {A} aggregate chunks across {W} workers.",
            matchChunks.Count, aggChunks.Count, workerUrls.Length);

        var matchChannel = Channel.CreateBounded<string[]>(new BoundedChannelOptions(matchChunks.Count + 1)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
        // Unbounded + multi-writer: match results feed new aggregate work from concurrent tasks.
        var aggChannel   = Channel.CreateUnbounded<long[]>(new UnboundedChannelOptions
            { SingleWriter = false });

        foreach (var chunk in matchChunks) await matchChannel.Writer.WriteAsync(chunk);
        foreach (var chunk in aggChunks)   await aggChannel.Writer.WriteAsync(chunk);
        matchChannel.Writer.Complete();
        // aggChannel stays open — completed after all match lanes finish.

        var matchConcurrency = MatchConcurrency;
        var aggChunkSize = AggregateWayIdChunkSize;

        // ── Match lanes (one per worker, each with semaphore-limited concurrency) ──
        var matchTasks = workerUrls.Select(url => Task.Run(async () =>
        {
            var sem = new SemaphoreSlim(matchConcurrency, matchConcurrency);
            var inflight = new List<Task>();

            await foreach (var tripIds in matchChannel.Reader.ReadAllAsync(ct))
            {
                await sem.WaitAsync(ct);
                var captured = tripIds;
                inflight.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await workerClient.SendMatchJobAsync(url, new MatchJobRequest(captured), ct);
                        if (result is not null)
                        {
                            _logMatchChunkOk(logger, captured.Length, url, result.Matched, null);

                            // Feed newly matched ways straight into the aggregate channel.
                            if (result.MatchedWayIds is { Length: > 0 })
                                foreach (var chunk in Chunk(result.MatchedWayIds.ToList(), aggChunkSize))
                                    await aggChannel.Writer.WriteAsync(chunk, ct);

                            // Track failures: increment for failed trips, remove for successful ones.
                            if (result.FailedTripIds is { Length: > 0 })
                                foreach (var t in result.FailedTripIds)
                                    _tripFailures.AddOrUpdate(t, 1, (_, c) => c + 1);

                            var failedSet = result.FailedTripIds is { Length: > 0 }
                                ? new HashSet<string>(result.FailedTripIds)
                                : [];
                            foreach (var t in captured)
                                if (!failedSet.Contains(t))
                                    _tripFailures.TryRemove(t, out _);
                        }
                        else
                        {
                            _logMatchChunkFail(logger, captured.Length, url, null);
                        }
                    }
                    finally { sem.Release(); }
                }, ct));
            }

            await Task.WhenAll(inflight);
        }, ct)).ToArray();

        // ── Aggregate lanes (one per worker, sequential per lane) ──
        var aggTasks = workerUrls.Select(url => Task.Run(async () =>
        {
            await foreach (var wayIds in aggChannel.Reader.ReadAllAsync(ct))
            {
                var result = await workerClient.SendAggregateJobAsync(url, new AggregateJobRequest(wayIds), ct);
                if (result is not null) _logAggChunkOk(logger, wayIds.Length, url, result.Processed, null);
                else                   _logAggChunkFail(logger, wayIds.Length, url, null);
            }
        }, ct)).ToArray();

        // Once all match lanes finish, no more aggregate work will arrive — close the channel.
        await Task.WhenAll(matchTasks);
        aggChannel.Writer.Complete();

        // Now wait for the aggregate lanes to drain.
        await Task.WhenAll(aggTasks);
    }

    /// <summary>
    /// Dispatches aggregate-only jobs for a set of way IDs (used for follow-up after matching).
    /// </summary>
    private async Task DispatchAggregateAsync(
        List<long> ways, string[] workerUrls, CancellationToken ct)
    {
        var aggChunks = Chunk(ways, AggregateWayIdChunkSize);
        var aggChannel = Channel.CreateBounded<long[]>(new BoundedChannelOptions(aggChunks.Count + 1)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

        foreach (var chunk in aggChunks) await aggChannel.Writer.WriteAsync(chunk);
        aggChannel.Writer.Complete();

        await Task.WhenAll(workerUrls.Select(url => Task.Run(async () =>
        {
            await foreach (var wayIds in aggChannel.Reader.ReadAllAsync(ct))
            {
                var result = await workerClient.SendAggregateJobAsync(url, new AggregateJobRequest(wayIds), ct);
                if (result is not null) _logAggChunkOk(logger, wayIds.Length, url, result.Processed, null);
                else                   _logAggChunkFail(logger, wayIds.Length, url, null);
            }
        }, ct)));
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    private async Task<List<string>> GetUnmatchedTripIdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.SpeedObservations
            .Where(o => o.OsmWayId == null && !o.IsAggregated)
            .Select(o => o.TripId)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task<List<long>> GetUnaggregatedWayIdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null)
            .Select(o => o.OsmWayId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>Splits a list into chunks of at most <paramref name="size"/> elements.</summary>
    private static List<T[]> Chunk<T>(List<T> items, int size)
    {
        var arr    = items.Count > 0 ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(items) : default;
        var chunks = new List<T[]>((items.Count + size - 1) / size);
        for (int i = 0; i < arr.Length; i += size)
            chunks.Add(arr.Slice(i, Math.Min(size, arr.Length - i)).ToArray());
        return chunks;
    }
}
