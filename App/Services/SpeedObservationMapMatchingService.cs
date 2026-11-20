using Microsoft.EntityFrameworkCore;
using OpenTrafficDBData;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Continuous map-matching worker.
///
/// The queue holds unmatched observations (<c>OsmWayId IS NULL AND NOT IsAggregated</c>).
/// Each pass picks the oldest trips that have <b>new</b> observations
/// (<c>MatchAttempts = 0</c>), FIFO by observation Id, and matches all of their
/// unmatched observations. Leftovers from earlier attempts are therefore only retried
/// together with new points of the same trip, never on their own. This keeps the
/// pass window free of unmatchable leftovers and gives every trip a fair turn.
///
/// While a backlog exists, passes run back to back. When the queue is drained, the
/// worker polls every <c>Aggregation:IdlePollSeconds</c> (default 2 s), so new data is
/// processed within seconds instead of in large bursts.
///
/// Config keys:
///   Aggregation:IdlePollSeconds           — poll interval when the queue is empty (default: 2)
///   Aggregation:MapMatchingTripChunkSize  — trips per pass (default: 200)
/// </summary>
public class SpeedObservationMapMatchingService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SpeedObservationMapMatchingService> logger) : BackgroundService
{
    private TimeSpan IdlePoll                 => TimeSpan.FromSeconds(double.Parse(config["Aggregation:IdlePollSeconds"] ?? "2"));
    private int      MapMatchingTripChunkSize => int.Parse(config["Aggregation:MapMatchingTripChunkSize"] ?? "200");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "MapMatching service started. IdlePoll={IdlePoll}, TripChunk={TripChunk}.",
            IdlePoll, MapMatchingTripChunkSize);

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var tripCount = 0;
            try
            {
                tripCount = await ProcessOncePassAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "MapMatching pass failed.");
            }

            // A full chunk means more work is waiting: continue immediately.
            if (tripCount < MapMatchingTripChunkSize)
                await Task.Delay(IdlePoll, stoppingToken);
        }
    }

    /// <summary>
    /// Matches the next chunk of trips with new observations. Returns the number of trips processed.
    /// </summary>
    private async Task<int> ProcessOncePassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matching = scope.ServiceProvider.GetRequiredService<MapMatchingService>();

        var tripCap = MapMatchingTripChunkSize;

        // Trips with at least one observation that has never been attempted, oldest first.
        var nextTrips = db.SpeedObservations
            .Where(x => x.OsmWayId == null && !x.IsAggregated && x.MatchAttempts == 0)
            .GroupBy(x => x.TripId)
            .OrderBy(g => g.Min(x => x.Id))
            .Select(g => g.Key)
            .Take(tripCap);

        // All unmatched observations of these trips, so earlier leftovers get context from new points.
        var unmatched = await db.SpeedObservations
            .Where(o => o.OsmWayId == null && !o.IsAggregated && nextTrips.Contains(o.TripId))
            .AsNoTracking()
            .OrderBy(o => o.Timestamp)
            .ThenBy(o => o.Id)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return 0;

        var tripCount = unmatched.Select(o => o.TripId).Distinct().Count();
        var result = await matching.ProcessRangeAsync(unmatched, db, ct);
        logger.LogDebug("Matched {Count} observations across {TripCount} trips.", result.Matched, tripCount);
        return tripCount;
    }
}
