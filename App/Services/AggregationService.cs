using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Recurring in-process aggregation job.
///
/// Polls every 15 seconds and — when there is pending work — drains the queue by
/// repeatedly matching unmatched trips and aggregating unmatched-but-now-matched ways,
/// until no work remains. Then finalizes (stale cleanup) once per drain.
///
/// Config keys:
///   Aggregation:IntervalSeconds           — poll interval when idle (default: 15)
///   Aggregation:MapMatchingTripChunkSize  — trips per match pass (default: 200)
///   Aggregation:AggregateWayIdChunkSize   — ways per aggregate pass (default: 200)
/// </summary>
public class AggregationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<AggregationService> logger) : BackgroundService
{
    private TimeSpan Interval                  => TimeSpan.FromSeconds(double.Parse(config["Aggregation:IntervalSeconds"] ?? "15"));
    private int      MapMatchingTripChunkSize  => int.Parse(config["Aggregation:MapMatchingTripChunkSize"] ?? "200");
    private int      AggregateWayIdChunkSize   => int.Parse(config["Aggregation:AggregateWayIdChunkSize"] ?? "200");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Aggregation service started. Interval={Interval}, TripChunk={TripChunk}, WayChunk={WayChunk}.",
            Interval, MapMatchingTripChunkSize, AggregateWayIdChunkSize);

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Aggregation cycle failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>
    /// Processes all pending work in a loop until both queues are empty, then finalizes.
    /// Each iteration re-queries the queue so newly-ingested data is picked up within
    /// the same drain cycle (no need to wait for the next tick).
    /// </summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        var didAnyWork = false;

        while (!ct.IsCancellationRequested)
        {
            var (matchedCount, aggregatedCount) = await ProcessOncePassAsync(ct);
            if (matchedCount == 0 && aggregatedCount == 0) break;
            didAnyWork = true;
        }

        if (didAnyWork)
        {
            using var scope = scopeFactory.CreateScope();
            var aggregation = scope.ServiceProvider.GetRequiredService<TrafficAggregationService>();
            await aggregation.FinalizeAsync(ct);
        }
    }

    /// <summary>
    /// One pass: match one chunk of unmatched trips, then aggregate one chunk of
    /// unmatched-but-now-matched ways. Returns the counts so DrainAsync can decide
    /// whether to continue.
    /// </summary>
    private async Task<(int Matched, int Aggregated)> ProcessOncePassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db          = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matching    = scope.ServiceProvider.GetRequiredService<MapMatchingService>();
        var aggregation = scope.ServiceProvider.GetRequiredService<TrafficAggregationService>();

        var tripChunk = await db.SpeedObservations
            .Where(o => o.OsmWayId == null && !o.IsAggregated)
            .Select(o => o.TripId)
            .Distinct()
            .Take(MapMatchingTripChunkSize)
            .ToArrayAsync(ct);

        var matched = 0;
        if (tripChunk.Length > 0)
        {
            var result = await matching.ProcessRangeAsync(tripChunk, db, ct);
            matched = result.Matched;
            logger.LogDebug("Matched {Count} observations across {TripCount} trips.", matched, tripChunk.Length);
        }

        var wayChunk = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null)
            .Select(o => o.OsmWayId!.Value)
            .Distinct()
            .Take(AggregateWayIdChunkSize)
            .ToArrayAsync(ct);

        var aggregated = 0;
        if (wayChunk.Length > 0)
        {
            var result = await aggregation.ReaggregateRangeAsync(wayChunk, ct);
            aggregated = result.Processed;
            logger.LogDebug("Aggregated {Count} observations across {WayCount} ways.", aggregated, wayChunk.Length);
        }

        return (matched, aggregated);
    }
}
