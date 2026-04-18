using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Periodically clusters unprocessed <see cref="HazardObservation"/> rows into
/// <see cref="HazardAggregate"/> rows and map-matches each aggregate's centroid
/// to an OSM edge via <see cref="HazardMapMatchingService"/>.
///
/// Clustering key: (ClusterGridX, ClusterGridY, HeadingBucket, HazardType) —
/// a ~30 m grid with eight 45° heading sectors so that an intersection can host
/// several distinct hazards (e.g. four red-light cameras pointing different ways).
///
/// Map-matching runs at most once per aggregate per cycle and only when the
/// centroid has drifted beyond <see cref="RematchDriftMeters"/> from the last
/// matched centroid — all candidates for a cycle are sent in a single OTR bulk call.
/// </summary>
public class HazardAggregationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<HazardAggregationService> logger) : BackgroundService
{
    private TimeSpan CycleInterval =>
        TimeSpan.FromSeconds(int.TryParse(config["Hazards:AggregationIntervalSeconds"], out var s) ? s : 30);

    /// <summary>Grid size in meters — reports whose snapped cell matches land in the same cluster.</summary>
    private double ClusterGridMeters =>
        double.TryParse(config["Hazards:ClusterGridMeters"], out var m) ? m : 30.0;

    /// <summary>Width of each heading bucket in degrees. 45° → 8 buckets.</summary>
    private const double HeadingBucketDegrees = 45.0;
    private const int HeadingBucketCount = (int)(360.0 / HeadingBucketDegrees);

    /// <summary>
    /// Centroid movement threshold (meters) that triggers re-map-matching of an
    /// existing aggregate. Smaller than the grid so that sub-cell centroid drift
    /// across several reports re-runs the match.
    /// </summary>
    private double RematchDriftMeters =>
        double.TryParse(config["Hazards:RematchDriftMeters"], out var m) ? m : 10.0;

    /// <summary>Max observations pulled per cycle. Caps OTR batch size and DB pressure.</summary>
    private int MaxObservationsPerCycle =>
        int.TryParse(config["Hazards:MaxObservationsPerCycle"], out var n) ? n : 5_000;

    /// <summary>Number of confirmations in the last 90 days required to mark a hazard as permanent.</summary>
    private const int PermanentConfirmationThreshold = 3;

    /// <summary>Approximate meters per degree of latitude — used only for the lon scaling factor.</summary>
    private const double MetersPerDegreeLat = 111_320.0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the DB and OTR settle before the first pass.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hazard aggregation cycle failed");
            }

            await Task.Delay(CycleInterval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matcher = scope.ServiceProvider.GetRequiredService<HazardMapMatchingService>();

        var observations = await db.HazardObservations
            .Where(o => !o.IsProcessed)
            .OrderBy(o => o.Id)
            .Take(MaxObservationsPerCycle)
            .ToListAsync(ct);

        if (observations.Count == 0) return;

        // Group reports by (grid-cell, heading-bucket, type). One group == one aggregate.
        var divisorLat = MetersPerDegreeLat / ClusterGridMeters;
        var groups = observations
            .GroupBy(o => ClusterKey(o, divisorLat))
            .ToList();

        var now = DateTime.UtcNow;
        var rematchDriftDegSquared = Math.Pow(RematchDriftMeters / MetersPerDegreeLat, 2);

        // Collect aggregates that need a (re-)match this cycle; we'll issue one OTR bulk call.
        var toMatch = new List<(HazardAggregate agg, double centroidLat, double centroidLon)>();

        foreach (var group in groups)
        {
            var key = group.Key;
            var reports = group.ToList();

            var existing = await db.HazardAggregates
                .FirstOrDefaultAsync(a =>
                    a.ClusterGridX == key.X &&
                    a.ClusterGridY == key.Y &&
                    a.HeadingBucket == key.HeadingBucket &&
                    a.HazardType == key.HazardType, ct);

            var agg = existing ?? new HazardAggregate
            {
                ClusterGridX     = key.X,
                ClusterGridY     = key.Y,
                HeadingBucket    = key.HeadingBucket,
                HazardType       = key.HazardType,
                FirstReportedAt  = reports.Min(r => r.Timestamp),
            };

            ApplyReportsToAggregate(agg, reports, now);

            if (existing == null)
                db.HazardAggregates.Add(agg);

            // Decide whether this aggregate needs map-matching this cycle.
            var needsMatch = agg.OsmWayId == null
                || agg.LastMatchedCentroidLat == null
                || agg.LastMatchedCentroidLon == null
                || SquaredDegreeDistance(
                    agg.Latitude, agg.Longitude,
                    agg.LastMatchedCentroidLat.Value, agg.LastMatchedCentroidLon.Value)
                   > rematchDriftDegSquared;

            if (needsMatch)
                toMatch.Add((agg, agg.Latitude, agg.Longitude));
        }

        // Persist the aggregate mutations before we kick off map-matching so
        // that if OTR is slow/unavailable, the clustering progress isn't lost.
        foreach (var obs in observations)
            obs.IsProcessed = true;

        await db.SaveChangesAsync(ct);

        if (toMatch.Count == 0)
        {
            logger.LogInformation(
                "Hazard aggregation: {Obs} reports → {Groups} aggregates, no re-match needed",
                observations.Count, groups.Count);
            return;
        }

        var points = toMatch
            .Select(x => new HazardMapMatchingService.LocalizePoint(
                x.centroidLat, x.centroidLon, x.agg.Heading))
            .ToList();

        var results = await matcher.LocalizeBulkAsync(points, ct);

        // Second save pass: apply match results to the same tracked entities.
        for (int i = 0; i < toMatch.Count; i++)
        {
            var (agg, cLat, cLon) = toMatch[i];
            var res = results[i];

            if (!res.Matched) continue;

            agg.OsmWayId               = res.OsmWayId;
            agg.MatchedLatitude        = res.SnapLat;
            agg.MatchedLongitude       = res.SnapLon;
            agg.LastMatchedCentroidLat = cLat;
            agg.LastMatchedCentroidLon = cLon;
            agg.LastMatchedAt          = now;
        }

        // Fill in street names in one query for the newly matched ways.
        var newlyMatchedWayIds = toMatch
            .Select(t => t.agg.OsmWayId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (newlyMatchedWayIds.Count > 0)
        {
            var names = await db.WayGeometries
                .Where(w => newlyMatchedWayIds.Contains(w.OsmWayId))
                .Select(w => new { w.OsmWayId, w.Name })
                .ToDictionaryAsync(w => w.OsmWayId, w => w.Name, ct);

            foreach (var (agg, _, _) in toMatch)
            {
                if (agg.OsmWayId.HasValue && names.TryGetValue(agg.OsmWayId.Value, out var name))
                    agg.StreetName = name;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Hazard aggregation: {Obs} reports → {Groups} aggregates, {Matched}/{MatchCandidates} matched",
            observations.Count, groups.Count, results.Count(r => r.Matched), toMatch.Count);
    }

    /// <summary>
    /// Merge a batch of reports into a (possibly brand-new) aggregate in a single pass,
    /// updating the circular-mean heading sums and the centroid.
    /// </summary>
    private static void ApplyReportsToAggregate(HazardAggregate agg, List<HazardObservation> reports, DateTime now)
    {
        // Centroid is tracked as a running mean; recompute from current state + new reports.
        var existingCount = agg.ConfirmationCount;
        var existingLatSum = agg.Latitude * existingCount;
        var existingLonSum = agg.Longitude * existingCount;

        foreach (var r in reports)
        {
            existingLatSum += r.Latitude;
            existingLonSum += r.Longitude;

            var rad = r.Heading * Math.PI / 180.0;
            agg.HeadingSinSum += Math.Sin(rad);
            agg.HeadingCosSum += Math.Cos(rad);

            if (!string.IsNullOrEmpty(r.Metadata))
                agg.Metadata = r.Metadata; // most-recent wins, matches pre-existing semantics
        }

        var newCount = existingCount + reports.Count;
        agg.Latitude = existingLatSum / newCount;
        agg.Longitude = existingLonSum / newCount;

        var avgRad = Math.Atan2(agg.HeadingSinSum, agg.HeadingCosSum);
        var avgDeg = avgRad * 180.0 / Math.PI;
        if (avgDeg < 0) avgDeg += 360;
        agg.Heading = (float)avgDeg;

        agg.ConfirmationCount = newCount;
        agg.LastConfirmedAt   = now;
        agg.ExpiresAt         = now + agg.HazardType.GetDefaultExpiry();
        agg.IsPermanent       = newCount >= PermanentConfirmationThreshold
                                && agg.FirstReportedAt >= now.AddDays(-90);
    }

    private readonly record struct ClusterKeyTuple(int X, int Y, int HeadingBucket, HazardType HazardType);

    private static ClusterKeyTuple ClusterKey(HazardObservation obs, double divisorLat)
    {
        // Scale lon by cos(lat) so grid cells are ~square in meters at the observation's latitude.
        var cosLat = Math.Cos(obs.Latitude * Math.PI / 180.0);
        if (cosLat < 0.01) cosLat = 0.01;
        var divisorLon = divisorLat * cosLat;

        var x = (int)Math.Floor(obs.Latitude * divisorLat);
        var y = (int)Math.Floor(obs.Longitude * divisorLon);
        var bucket = ((int)Math.Floor(obs.Heading / HeadingBucketDegrees)) % HeadingBucketCount;
        if (bucket < 0) bucket += HeadingBucketCount;
        return new ClusterKeyTuple(x, y, bucket, obs.HazardType);
    }

    private static double SquaredDegreeDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = lat1 - lat2;
        var dLon = lon1 - lon2;
        return dLat * dLat + dLon * dLon;
    }
}
