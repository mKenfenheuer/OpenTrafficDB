using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.AggregateWorker.Services;

public class TrafficAggregationService(AppDbContext db)
{

    // ── Traffic jam estimation ───────────────────────────────────────────────────

    public static double ComputeJam(double delay) => delay switch
    {
        <= 0    => 0.0,
        <= 0.6  => delay * (0.7 / 0.6),
        <= 0.85 => 0.7 + (delay - 0.6) * (0.3 / 0.25),
        _       => 1.0
    };

    // ── Time-bucket helpers ─────────────────────────────────────────────────────

    private static int ToFiveMinuteBucket(DateTime utc)
        => (utc.Hour * 60 + utc.Minute) / 5;

    private static readonly TimeSpan LiveRetention      = TimeSpan.FromHours(1);
    private static readonly TimeSpan UnmatchedRetention = TimeSpan.FromHours(24);

    // ── Hybrid aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates matched observations in [from, to) time window using server-side SQL upserts.
    /// Does NOT run cleanup — call <see cref="FinalizeAsync"/> after all slots complete.
    /// Returns the number of observations processed.
    /// </summary>
    public async Task<AggregateJobResult> ReaggregateRangeAsync(
        string[] tripIds, CancellationToken ct = default)
    {
        // Collect affected way IDs before marking rows aggregated.
        var affectedWayIds = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && tripIds.Contains(o.TripId))
            .Select(o => o.OsmWayId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (affectedWayIds.Count == 0) return new AggregateJobResult(0);

        // Upsert way-level aggregates entirely in SQL — no rows loaded into .NET.
        // ORDER BY in the subquery ensures all concurrent transactions acquire index locks
        // in consistent key order, preventing deadlocks on WaySpeedAggregates.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "WaySpeedAggregates"
                ("OsmWayId", "DayOfWeek", "FiveMinuteBucket",
                 "SumSpeedKmh", "SampleCount", "SpeedLimitKmh", "LastUpdated", "FirstSeenAt")
            SELECT way_id, dow, bucket, sum_speed, sample_count, speed_limit, NOW(), NOW()
            FROM (
                SELECT
                    "OsmWayId"                                                        AS way_id,
                    EXTRACT(DOW FROM "Timestamp")::int                                AS dow,
                    (EXTRACT(HOUR FROM "Timestamp")::int * 60
                        + EXTRACT(MINUTE FROM "Timestamp")::int) / 5                 AS bucket,
                    SUM("SpeedMs" * 3.6)                                              AS sum_speed,
                    COUNT(*)::int                                                     AS sample_count,
                    MAX("SpeedLimitKmh")                                              AS speed_limit
                FROM "SpeedObservations"
                WHERE NOT "IsAggregated"
                  AND "OsmWayId" IS NOT NULL
                  AND "TripId"   = ANY({tripIds})
                GROUP BY
                    "OsmWayId",
                    EXTRACT(DOW FROM "Timestamp"),
                    (EXTRACT(HOUR FROM "Timestamp")::int * 60
                        + EXTRACT(MINUTE FROM "Timestamp")::int) / 5
                ORDER BY way_id, dow, bucket
            ) agg
            ON CONFLICT ("OsmWayId", "DayOfWeek", "FiveMinuteBucket") DO UPDATE SET
                "SumSpeedKmh"   = "WaySpeedAggregates"."SumSpeedKmh"  + EXCLUDED."SumSpeedKmh",
                "SampleCount"   = "WaySpeedAggregates"."SampleCount"  + EXCLUDED."SampleCount",
                "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh", "WaySpeedAggregates"."SpeedLimitKmh"),
                "LastUpdated"   = NOW()
            """, ct);

        // Mark all processed observations aggregated with a single UPDATE.
        var processed = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && tripIds.Contains(o.TripId))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsAggregated, true), ct);

        await RebuildSummariesAsync(affectedWayIds, ct);

        return new AggregateJobResult(processed);
    }

    /// <summary>
    /// Runs global cleanup after all aggregation slots complete:
    /// refreshes constrained speeds and deletes observations outside retention windows.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        await RefreshConstrainedSpeedsAsync(ct);

        var liveWindow      = DateTime.UtcNow - LiveRetention;
        var unmatchedCutoff = DateTime.UtcNow - UnmatchedRetention;

        await db.SpeedObservations
            .Where(o => (o.IsAggregated && o.CreatedAt < liveWindow)
                     || (o.OsmWayId == null && o.CreatedAt < unmatchedCutoff))
            .ExecuteDeleteAsync(ct);
    }

    // ── Live traffic (rolling 30-minute window) ─────────────────────────────────

    public async Task<LiveTrafficResponse> GetLiveSpeedsAsync(LiveTrafficQuery query, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-query.MaxAgeMinutes);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff
                && o.Latitude >= query.MinLat && o.Latitude <= query.MaxLat
                && o.Longitude >= query.MinLon && o.Longitude <= query.MaxLon)
            .OrderByDescending(o => o.Timestamp)
            .Take(10_000)
            .AsNoTracking()
            .Select(o => new LiveSpeedPoint
            {
                Latitude      = o.Latitude,
                Longitude     = o.Longitude,
                SpeedMs       = o.SpeedMs,
                SpeedKmh      = Math.Round(o.SpeedMs * 3.6, 1),
                Heading       = o.Heading,
                OsmWayId      = o.OsmWayId,
                SpeedLimitKmh = o.SpeedLimitKmh,
                Timestamp     = o.Timestamp
            })
            .ToListAsync(ct);

        foreach (var pt in obs)
        {
            if (pt.SpeedLimitKmh is > 0)
            {
                var delay = Math.Clamp(1.0 - pt.SpeedKmh / pt.SpeedLimitKmh.Value, 0.0, 1.0);
                pt.DelayFactor = Math.Round(delay, 3);
                pt.JamEstimate = Math.Round(ComputeJam(delay), 3);
            }
        }

        return new LiveTrafficResponse { Points = obs, TotalPoints = obs.Count, AsOf = DateTime.UtcNow };
    }

    public async Task WriteCurrentWayCsvAsync(Stream output, CancellationToken ct = default)
    {
        var now    = DateTime.UtcNow;
        var dow    = (int)now.DayOfWeek;
        var bucket = ToFiveMinuteBucket(now);
        var cutoff = now.AddMinutes(-30);

        var historic = await db.WaySpeedAggregates
            .Where(a => a.DayOfWeek == dow && a.FiveMinuteBucket == bucket)
            .AsNoTracking()
            .ToListAsync(ct);

        var liveRaw = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null)
            .AsNoTracking()
            .Select(o => new { o.OsmWayId, SpeedKmh = o.SpeedMs * 3.6, o.SpeedLimitKmh })
            .ToListAsync(ct);

        var liveByWay = liveRaw
            .GroupBy(o => o.OsmWayId!.Value)
            .ToDictionary(g => g.Key, g => (AvgKmh: g.Average(x => x.SpeedKmh), Count: g.Count(), SpeedLimit: g.FirstOrDefault(x => x.SpeedLimitKmh != null)?.SpeedLimitKmh));

        var historicByWay = historic.ToDictionary(a => a.OsmWayId);
        var wayIdSet      = historicByWay.Keys.Union(liveByWay.Keys).ToList();

        var summaries = await db.WaySpeedSummaries
            .Where(s => wayIdSet.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var allWayIds = historicByWay.Keys.Union(liveByWay.Keys);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,speed_kmh,speed_limit_kmh,delay_factor,jam_estimate,source,live_samples,historic_samples");

        foreach (var wayId in allWayIds.OrderBy(id => id))
        {
            var hasLive     = liveByWay.TryGetValue(wayId, out var live);
            var hasHistoric = historicByWay.TryGetValue(wayId, out var hist);

            double speedKmh;
            string source;
            int liveSamples     = hasLive ? live.Count : 0;
            int historicSamples = hasHistoric && hist!.SampleCount > 0 ? hist.SampleCount : 0;

            if (hasLive) { speedKmh = live.AvgKmh; source = "live"; }
            else
            {
                var freeflow = summaries.TryGetValue(wayId, out var s) ? s.FreeflowSpeed : (hist!.SpeedLimitKmh ?? 0);
                speedKmh = BlendedAvg(hist!.SumSpeedKmh, hist.SampleCount, hist.FirstSeenAt, freeflow);
                source = "historic";
            }

            speedKmh = Math.Round(speedKmh, 1);
            var speedLimit = (hasLive ? live.SpeedLimit : null) ?? (hasHistoric ? hist!.SpeedLimitKmh : null);

            double? delayFactor = null, jamEstimate = null;
            if (speedLimit is > 0)
            {
                var delay = Math.Clamp(1.0 - speedKmh / speedLimit.Value, 0.0, 1.0);
                delayFactor = Math.Round(delay, 3);
                jamEstimate = Math.Round(ComputeJam(delay), 3);
            }

            await writer.WriteLineAsync($"{wayId},{speedKmh:F1},{speedLimit?.ToString("F1") ?? ""},{delayFactor?.ToString("F3") ?? ""},{jamEstimate?.ToString("F3") ?? ""},{source},{liveSamples},{historicSamples}");
        }
    }

    public async Task WriteLiveWayCsvAsync(Stream output, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null)
            .AsNoTracking()
            .Select(o => new { o.OsmWayId, o.SpeedMs })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,avg_speed_kmh,sample_count");

        foreach (var g in obs.GroupBy(o => o.OsmWayId!.Value).OrderBy(g => g.Key))
            await writer.WriteLineAsync($"{g.Key},{g.Average(o => o.SpeedMs) * 3.6:F1},{g.Count()}");
    }

    public async Task WriteHistoricWayCsvAsync(Stream output, CancellationToken ct = default)
    {
        var summaries = await db.WaySpeedSummaries
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,speed,time");

        await foreach (var a in db.WaySpeedAggregates
            .OrderBy(a => a.OsmWayId).ThenBy(a => a.DayOfWeek).ThenBy(a => a.FiveMinuteBucket)
            .AsNoTracking()
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            var speed = a.SampleCount > 0
                ? BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, summaries.TryGetValue(a.OsmWayId, out var s) ? s.FreeflowSpeed : (a.SpeedLimitKmh ?? 0))
                : 0.0;
            await writer.WriteLineAsync($"{a.OsmWayId},{speed:F1},{a.DayOfWeek * 288 + a.FiveMinuteBucket}");
        }
    }


    public async Task<Dictionary<long, WayTrafficData>> GetCurrentWayTrafficAsync(IEnumerable<long> wayIds, CancellationToken ct = default)
    {
        var ids = wayIds.ToList();
        if (ids.Count == 0) return [];

        var now    = DateTime.UtcNow;
        var dow    = (int)now.DayOfWeek;
        var bucket = ToFiveMinuteBucket(now);
        var cutoff = now.AddMinutes(-30);

        var historic = await db.WaySpeedAggregates
            .Where(a => ids.Contains(a.OsmWayId) && a.DayOfWeek == dow && a.FiveMinuteBucket == bucket)
            .AsNoTracking()
            .ToListAsync(ct);

        var liveRaw = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null && ids.Contains(o.OsmWayId!.Value))
            .AsNoTracking()
            .Select(o => new { o.OsmWayId, SpeedKmh = o.SpeedMs * 3.6, o.SpeedLimitKmh })
            .ToListAsync(ct);

        var liveByWay     = liveRaw.GroupBy(o => o.OsmWayId!.Value).ToDictionary(g => g.Key, g => (AvgKmh: g.Average(x => x.SpeedKmh), Count: g.Count(), SpeedLimit: g.FirstOrDefault(x => x.SpeedLimitKmh != null)?.SpeedLimitKmh));
        var historicByWay = historic.ToDictionary(a => a.OsmWayId);
        var allIds        = historicByWay.Keys.Union(liveByWay.Keys).ToList();

        var summaries = await db.WaySpeedSummaries
            .Where(s => allIds.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var result = new Dictionary<long, WayTrafficData>();
        foreach (var wayId in allIds)
        {
            var hasLive     = liveByWay.TryGetValue(wayId, out var live);
            var hasHistoric = historicByWay.TryGetValue(wayId, out var hist);

            double speedKmh;
            string source;
            int liveSamples     = hasLive ? live.Count : 0;
            int historicSamples = hasHistoric && hist!.SampleCount > 0 ? hist.SampleCount : 0;

            if (hasLive) { speedKmh = live.AvgKmh; source = "live"; }
            else
            {
                var freeflow = summaries.TryGetValue(wayId, out var s) ? s.FreeflowSpeed : (hist!.SpeedLimitKmh ?? 0);
                speedKmh = BlendedAvg(hist!.SumSpeedKmh, hist.SampleCount, hist.FirstSeenAt, freeflow);
                source = "historic";
            }

            speedKmh = Math.Round(speedKmh, 1);
            var speedLimit = (hasLive ? live.SpeedLimit : null) ?? (hasHistoric ? hist!.SpeedLimitKmh : null);

            double? delayFactor = null, jamEstimate = null;
            if (speedLimit is > 0)
            {
                var delay = Math.Clamp(1.0 - speedKmh / speedLimit.Value, 0.0, 1.0);
                delayFactor = Math.Round(delay, 3);
                jamEstimate = Math.Round(ComputeJam(delay), 3);
            }

            result[wayId] = new WayTrafficData(speedKmh, speedLimit, delayFactor, jamEstimate, source, liveSamples, historicSamples);
        }
        return result;
    }

    // ── WaySpeedSummary maintenance ─────────────────────────────────────────────

    private static double BlendedAvg(double sumSpeedKmh, int sampleCount, DateTime firstSeenAt, double freeflowSpeed)
    {
        var totalSlots = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - firstSeenAt).TotalDays / 7.0));
        var missing    = Math.Max(0, totalSlots - sampleCount);
        return (sumSpeedKmh + missing * freeflowSpeed) / (sampleCount + missing);
    }

    /// <summary>
    /// Rebuilds WaySpeedSummary rows for the given ways using server-side
    /// percentile_cont — avoids loading all aggregate buckets into .NET.
    /// </summary>
    private async Task RebuildSummariesAsync(List<long> wayIds, CancellationToken ct)
    {
        if (wayIds.Count == 0) return;

        var wayIdArray = wayIds.ToArray();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "WaySpeedSummaries"
                ("OsmWayId", "FreeflowSpeed", "P50Speed", "P95Speed",
                 "SpeedLimitKmh", "TotalSamples", "LastUpdated")
            SELECT
                "OsmWayId",
                GREATEST(
                    percentile_cont(0.85) WITHIN GROUP (ORDER BY "SumSpeedKmh" / NULLIF("SampleCount", 0)),
                    COALESCE(MAX("SpeedLimitKmh"), 0) * 0.75
                ),
                percentile_cont(0.50) WITHIN GROUP (ORDER BY "SumSpeedKmh" / NULLIF("SampleCount", 0)),
                percentile_cont(0.95) WITHIN GROUP (ORDER BY "SumSpeedKmh" / NULLIF("SampleCount", 0)),
                MAX("SpeedLimitKmh"),
                SUM("SampleCount")::int,
                NOW()
            FROM "WaySpeedAggregates"
            WHERE "OsmWayId" = ANY({wayIdArray}) AND "SampleCount" > 0
            GROUP BY "OsmWayId"
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "FreeflowSpeed" = EXCLUDED."FreeflowSpeed",
                "P50Speed"      = EXCLUDED."P50Speed",
                "P95Speed"      = EXCLUDED."P95Speed",
                "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh", "WaySpeedSummaries"."SpeedLimitKmh"),
                "TotalSamples"  = EXCLUDED."TotalSamples",
                "LastUpdated"   = NOW()
            """, ct);
    }

    /// <summary>
    /// Refreshes ConstrainedSpeed on all WaySpeedSummary rows using two
    /// server-side SQL UPDATEs — avoids loading all summaries and observations into memory.
    /// </summary>
    private async Task RefreshConstrainedSpeedsAsync(CancellationToken ct)
    {
        // Set constrained speed for ways that have live observations
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "WaySpeedSummaries" s
            SET    "ConstrainedSpeed" = live.avg_speed,
                   "LastUpdated"      = NOW()
            FROM (
                SELECT "OsmWayId",
                       ROUND((AVG("SpeedMs") * 3.6)::numeric, 1) AS avg_speed
                FROM   "SpeedObservations"
                WHERE  "Timestamp" >= NOW() - INTERVAL '30 minutes'
                  AND  "OsmWayId"  IS NOT NULL
                GROUP  BY "OsmWayId"
            ) live
            WHERE s."OsmWayId" = live."OsmWayId"
            """, ct);

        // Clear constrained speed for ways with no recent live data.
        // NOT EXISTS uses the (Timestamp, OsmWayId) index; avoids the full DISTINCT scan of NOT IN.
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "WaySpeedSummaries" s
            SET    "ConstrainedSpeed" = NULL,
                   "LastUpdated"      = NOW()
            WHERE  s."ConstrainedSpeed" IS NOT NULL
              AND  NOT EXISTS (
                       SELECT 1
                       FROM   "SpeedObservations" o
                       WHERE  o."OsmWayId"  = s."OsmWayId"
                         AND  o."Timestamp" >= NOW() - INTERVAL '30 minutes'
                   )
            """, ct);
    }

    // ── Valhalla CSV export ─────────────────────────────────────────────────────

    public async Task WriteValhallaExportCsvAsync(Stream output, CancellationToken ct = default)
    {
        var summaries = await db.WaySpeedSummaries
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("way_id,freeflow_speed,constrained_speed,historical_speeds");

        var speedBuffer    = new float[2016];
        var slots          = new Dictionary<int, double>();
        var writtenWays    = new HashSet<long>();
        long? currentWayId = null;
        double currentFreeflow = 0;

        async Task FlushWayAsync(long wayId)
        {
            summaries.TryGetValue(wayId, out var summary);
            var freeflow    = summary is not null ? (int)Math.Round(summary.FreeflowSpeed) : 0;
            var constrained = summary is not null ? (int)Math.Round(summary.ConstrainedSpeed ?? summary.P50Speed) : freeflow;
            var fill = (float)freeflow;
            for (var i = 0; i < 2016; i++)
                speedBuffer[i] = slots.TryGetValue(i, out var sv) ? (float)sv : fill;
            await writer.WriteLineAsync($"{wayId},{freeflow},{constrained},{ValhallaSpeedEncoder.Encode(speedBuffer)}");
            writtenWays.Add(wayId);
        }

        // Stream aggregates ordered by way — accumulate slots per way, flush on boundary.
        await foreach (var b in db.WaySpeedAggregates
            .Where(a => a.SampleCount > 0)
            .OrderBy(a => a.OsmWayId)
            .AsNoTracking()
            .Select(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket, a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            if (currentWayId != b.OsmWayId)
            {
                if (currentWayId.HasValue) await FlushWayAsync(currentWayId.Value);
                currentWayId    = b.OsmWayId;
                currentFreeflow = summaries.TryGetValue(b.OsmWayId, out var sum) ? sum.FreeflowSpeed : 0;
                slots.Clear();
            }
            slots[(b.DayOfWeek + 6) % 7 * 288 + b.FiveMinuteBucket] =
                BlendedAvg(b.SumSpeedKmh, b.SampleCount, b.FirstSeenAt, currentFreeflow);
        }
        if (currentWayId.HasValue) await FlushWayAsync(currentWayId.Value);

        // Write summary-only ways (have a summary entry but no aggregate buckets yet).
        foreach (var wayId in summaries.Keys.Where(id => !writtenWays.Contains(id)).OrderBy(id => id))
        {
            slots.Clear();
            await FlushWayAsync(wayId);
        }
    }

}
