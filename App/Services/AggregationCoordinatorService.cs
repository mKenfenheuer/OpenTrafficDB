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
    private TimeSpan Interval  => TimeSpan.FromSeconds(double.Parse(config["Coordinator:IntervalSeconds"] ?? "60"));
    private int      ChunkSize => int.Parse(config["Coordinator:ChunkSize"] ?? "200");

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Aggregation coordinator started. Interval: {Interval}, ChunkSize: {Chunk}.",
            Interval, ChunkSize);

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try   { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Coordinator cycle failed."); }

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

        // Discover both sets in parallel — they are disjoint (match=OsmWayId IS NULL,
        // aggregate=OsmWayId IS NOT NULL) so the two discovery queries are independent.
        var unmatchedTask  = GetUnmatchedTripIdsAsync(ct);
        var unaggregatedTask = GetUnaggregatedWayIdsAsync(ct);
        await Task.WhenAll(unmatchedTask, unaggregatedTask);
        var unmatchedTrips   = unmatchedTask.Result;
        var unaggregatedWays = unaggregatedTask.Result;

        _logCycle(logger, unmatchedTrips.Count, unaggregatedWays.Count, workerUrls.Length, null);

        // ── 1+2. Match and aggregate interleaved across workers ───────────────
        if (unmatchedTrips.Count > 0 || unaggregatedWays.Count > 0)
            await DispatchInterleavedAsync(unmatchedTrips, unaggregatedWays, workerUrls, ct);

        // ── 3. Finalize — only needed when aggregation ran ────────────────────
        if (unaggregatedWays.Count > 0)
            await workerClient.SendFinalizeAsync(ct);
    }

    /// <summary>
    /// Dispatches match and aggregate chunks to workers via two independent channels.
    /// Each worker runs a match lane and an aggregate lane concurrently, so Valhalla I/O
    /// and DB aggregation overlap — neither waits on the other.
    /// </summary>
    private async Task DispatchInterleavedAsync(
        List<string> trips, List<long> ways, string[] workerUrls, CancellationToken ct)
    {
        var matchChunks = Chunk(trips, ChunkSize);
        var aggChunks   = Chunk(ways,  ChunkSize);

        logger.LogInformation("Dispatching {M} match + {A} aggregate chunks across {W} workers.",
            matchChunks.Count, aggChunks.Count, workerUrls.Length);

        var matchChannel = Channel.CreateUnbounded<string[]>();
        var aggChannel   = Channel.CreateUnbounded<long[]>();

        foreach (var chunk in matchChunks) matchChannel.Writer.TryWrite(chunk);
        foreach (var chunk in aggChunks)   aggChannel.Writer.TryWrite(chunk);
        matchChannel.Writer.Complete();
        aggChannel.Writer.Complete();

        await Task.WhenAll(workerUrls.Select(url => Task.Run(async () =>
        {
            // Each worker runs both lanes simultaneously — match and aggregate in parallel.
            var matchLane = Task.Run(async () =>
            {
                await foreach (var tripIds in matchChannel.Reader.ReadAllAsync(ct))
                {
                    var result = await workerClient.SendMatchJobAsync(url, new MatchJobRequest(tripIds), ct);
                    if (result is not null) _logMatchChunkOk(logger, tripIds.Length, url, result.Matched, null);
                    else                   _logMatchChunkFail(logger, tripIds.Length, url, null);
                }
            }, ct);

            var aggLane = Task.Run(async () =>
            {
                await foreach (var wayIds in aggChannel.Reader.ReadAllAsync(ct))
                {
                    var result = await workerClient.SendAggregateJobAsync(url, new AggregateJobRequest(wayIds), ct);
                    if (result is not null) _logAggChunkOk(logger, wayIds.Length, url, result.Processed, null);
                    else                   _logAggChunkFail(logger, wayIds.Length, url, null);
                }
            }, ct);

            await Task.WhenAll(matchLane, aggLane);
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
