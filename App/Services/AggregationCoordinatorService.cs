using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Coordinator that queries all pending trip IDs, splits them evenly across all
/// healthy AggregateWorker instances, and dispatches non-overlapping job chunks.
/// Workers never process the same trip — no DB conflicts possible.
///
/// Config keys:
///   Coordinator:ChunkSize       — trips per job chunk (default: 200)
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
        var unmatchedTrips     = await GetUnmatchedTripIdsAsync(ct);
        var unaggregatedTrips  = await GetUnaggregatedTripIdsAsync(ct);

        logger.LogInformation("Cycle: {Unmatched} unmatched, {Unaggregated} unaggregated trips, {Workers} workers.",
            unmatchedTrips.Count, unaggregatedTrips.Count, workerUrls.Length);

        // ── 1+2. Match and aggregate in parallel ──────────────────────────────
        var matchTask = unmatchedTrips.Count > 0
            ? DispatchMatchAsync(unmatchedTrips, workerUrls, ct)
            : Task.CompletedTask;

        var aggregateTask = unaggregatedTrips.Count > 0
            ? DispatchAggregateAsync(unaggregatedTrips, workerUrls, ct)
            : Task.CompletedTask;

        await Task.WhenAll(matchTask, aggregateTask);

        // ── 3. Finalize — only needed when aggregation ran ────────────────────
        if (unaggregatedTrips.Count > 0)
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
                    logger.LogDebug("Match chunk ({Count} trips) on {Url}: {Matched} matched.",
                        tripIds.Length, url, result.Matched);
                else
                    logger.LogWarning("Match chunk ({Count} trips) on {Url} failed.", tripIds.Length, url);
            }, ct);
    }

    private async Task DispatchAggregateAsync(List<string> trips, string[] workerUrls, CancellationToken ct)
    {
        var chunks = Chunk(trips, ChunkSize);
        logger.LogInformation("Dispatching {Chunks} aggregate chunks.", chunks.Count);
        await DispatchAsync(chunks, workerUrls,
            async (url, tripIds, token) =>
            {
                var result = await workerClient.SendAggregateJobAsync(url, new AggregateJobRequest(tripIds), token);
                if (result is not null)
                    logger.LogDebug("Aggregate chunk ({Count} trips) on {Url}: {Processed} processed.",
                        tripIds.Length, url, result.Processed);
                else
                    logger.LogWarning("Aggregate chunk ({Count} trips) on {Url} failed.", tripIds.Length, url);
            }, ct);
    }

    /// <summary>
    /// Fan-out chunks across workers: fills a channel, each worker pulls chunks
    /// sequentially so no two workers ever receive the same chunk.
    /// </summary>
    private static async Task DispatchAsync(
        List<string[]> chunks,
        string[] workerUrls,
        Func<string, string[], CancellationToken, Task> send,
        CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string[]>();
        foreach (var chunk in chunks) channel.Writer.TryWrite(chunk);
        channel.Writer.Complete();

        await Task.WhenAll(workerUrls.Select(url => Task.Run(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                await send(url, chunk, ct);
        }, ct)));
    }

    // ── Trip ID discovery ────────────────────────────────────────────────────

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

    private async Task<List<string>> GetUnaggregatedTripIdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null)
            .Select(o => o.TripId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>Splits a list into chunks of at most <paramref name="size"/> elements.</summary>
    private static List<string[]> Chunk(List<string> items, int size)
    {
        var chunks = new List<string[]>();
        for (int i = 0; i < items.Count; i += size)
            chunks.Add(items.Skip(i).Take(size).ToArray());
        return chunks;
    }
}
