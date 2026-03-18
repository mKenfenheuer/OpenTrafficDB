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

        // Discover both sets upfront — they are disjoint (match=OsmWayId IS NULL,
        // aggregate=OsmWayId IS NOT NULL) so the two dispatch phases are safe to run concurrently.
        var unmatchedTrips    = await GetUnmatchedTripIdsAsync(ct);
        var unaggregatedWays  = await GetUnaggregatedWayIdsAsync(ct);

        _logCycle(logger, unmatchedTrips.Count, unaggregatedWays.Count, workerUrls.Length, null);

        // ── 1+2. Match and aggregate in parallel ──────────────────────────────
        var matchTask = unmatchedTrips.Count > 0
            ? DispatchMatchAsync(unmatchedTrips, workerUrls, ct)
            : Task.CompletedTask;

        var aggregateTask = unaggregatedWays.Count > 0
            ? DispatchAggregateAsync(unaggregatedWays, workerUrls, ct)
            : Task.CompletedTask;

        await Task.WhenAll(matchTask, aggregateTask);

        // ── 3. Finalize — only needed when aggregation ran ────────────────────
        if (unaggregatedWays.Count > 0)
            await workerClient.SendFinalizeAsync(ct);
    }

    private async Task DispatchMatchAsync(List<string> trips, string[] workerUrls, CancellationToken ct)
    {
        var chunks = Chunk(trips, ChunkSize);
        logger.LogInformation("Dispatching {Chunks} match chunks.", chunks.Count);
        await DispatchAsync(chunks, workerUrls,
            async (url, tripIds, token) =>
            {
                var result = await workerClient.SendMatchJobAsync(url, new MatchJobRequest(tripIds), token);
                if (result is not null)
                    _logMatchChunkOk(logger, tripIds.Length, url, result.Matched, null);
                else
                    _logMatchChunkFail(logger, tripIds.Length, url, null);
            }, ct);
    }

    private async Task DispatchAggregateAsync(List<long> ways, string[] workerUrls, CancellationToken ct)
    {
        var chunks = Chunk(ways, ChunkSize);
        logger.LogInformation("Dispatching {Chunks} aggregate chunks.", chunks.Count);
        await DispatchAsync(chunks, workerUrls,
            async (url, wayIds, token) =>
            {
                var result = await workerClient.SendAggregateJobAsync(url, new AggregateJobRequest(wayIds), token);
                if (result is not null)
                    _logAggChunkOk(logger, wayIds.Length, url, result.Processed, null);
                else
                    _logAggChunkFail(logger, wayIds.Length, url, null);
            }, ct);
    }

    /// <summary>
    /// Fan-out chunks across workers: fills a channel, each worker pulls chunks
    /// sequentially so no two workers ever receive the same chunk.
    /// </summary>
    private static async Task DispatchAsync<T>(
        List<T[]> chunks,
        string[] workerUrls,
        Func<string, T[], CancellationToken, Task> send,
        CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<T[]>();
        foreach (var chunk in chunks) channel.Writer.TryWrite(chunk);
        channel.Writer.Complete();

        await Task.WhenAll(workerUrls.Select(url => Task.Run(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                await send(url, chunk, ct);
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
        var chunks = new List<T[]>();
        for (int i = 0; i < items.Count; i += size)
            chunks.Add(items.Skip(i).Take(size).ToArray());
        return chunks;
    }
}
