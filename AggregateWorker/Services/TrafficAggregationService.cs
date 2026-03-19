using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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

    private static readonly TimeSpan UnmatchedRetention = TimeSpan.FromHours(24);

    // ── Hybrid aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates matched observations in [from, to) time window using server-side SQL upserts.
    /// Does NOT run cleanup — call <see cref="FinalizeAsync"/> after all slots complete.
    /// Returns the number of observations processed.
    /// </summary>
    public async Task<AggregateJobResult> ReaggregateRangeAsync(
        long[] wayIds, CancellationToken ct = default)
    {
        if (wayIds.Length == 0) return new AggregateJobResult(0);

        // Chunks are partitioned by way ID at the coordinator, so no two concurrent jobs
        // share a way — no deadlocks possible on WaySpeedAggregates.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "WaySpeedAggregates"
                ("OsmWayId", "DayOfWeek", "FiveMinuteBucket",
                 "SumSpeedKmh", "SampleCount", "SpeedLimitKmh", "LastUpdated", "FirstSeenAt")
            SELECT
                "OsmWayId",
                EXTRACT(DOW FROM "Timestamp")::int,
                (EXTRACT(HOUR FROM "Timestamp")::int * 60
                    + EXTRACT(MINUTE FROM "Timestamp")::int) / 5,
                SUM("SpeedMs" * 3.6),
                COUNT(*)::int,
                MAX("SpeedLimitKmh"),
                NOW(), NOW()
            FROM "SpeedObservations"
            WHERE NOT "IsAggregated"
              AND "OsmWayId" = ANY({wayIds})
            GROUP BY
                "OsmWayId",
                EXTRACT(DOW FROM "Timestamp"),
                (EXTRACT(HOUR FROM "Timestamp")::int * 60
                    + EXTRACT(MINUTE FROM "Timestamp")::int) / 5
            ON CONFLICT ("OsmWayId", "DayOfWeek", "FiveMinuteBucket") DO UPDATE SET
                "SumSpeedKmh"   = "WaySpeedAggregates"."SumSpeedKmh"  + EXCLUDED."SumSpeedKmh",
                "SampleCount"   = "WaySpeedAggregates"."SampleCount"  + EXCLUDED."SampleCount",
                "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh", "WaySpeedAggregates"."SpeedLimitKmh"),
                "LastUpdated"   = NOW()
            """, ct);

        // Apply EMA to WaySpeedSummaries before observations are deleted, then delete.
        await UpdateSummariesEmaAsync(wayIds, ct);

        var processed = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && wayIds.Contains(o.OsmWayId.Value))
            .ExecuteDeleteAsync(ct);

        return new AggregateJobResult(processed);
    }

    /// <summary>
    /// Runs global cleanup after all aggregation slots complete:
    /// refreshes constrained speeds and deletes observations outside retention windows.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        // Delete stale unmatched observations (Valhalla never found a way for them).
        // Aggregated observations are deleted eagerly in ReaggregateRangeAsync.
        var unmatchedCutoff = DateTime.UtcNow - UnmatchedRetention;

        await db.SpeedObservations
            .Where(o => o.OsmWayId == null && o.CreatedAt < unmatchedCutoff)
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

    // ── WaySpeedSummary EMA maintenance ─────────────────────────────────────────

    private const double EmaAlpha = 0.8;

    private static double BlendedAvg(double sumSpeedKmh, int sampleCount, DateTime firstSeenAt, double freeflowSpeed)
    {
        var totalSlots = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - firstSeenAt).TotalDays / 7.0));
        var missing    = Math.Max(0, totalSlots - sampleCount);
        return (sumSpeedKmh + missing * freeflowSpeed) / (sampleCount + missing);
    }

    /// <summary>
    /// Updates WaySpeedSummaries for the given ways using an exponential moving average (α=0.8).
    /// Observations are applied oldest-first so the most recent reading has the highest weight.
    /// All summary fields (ConstrainedSpeed, FreeflowSpeed) are updated
    /// with the same rolling pattern: new = old * 0.2 + batch_value * 0.8.
    /// Must be called before the observations are deleted.
    /// </summary>
    private async Task UpdateSummariesEmaAsync(long[] wayIds, CancellationToken ct)
    {
        if (wayIds.Length == 0) return;

        // Load existing summaries as EMA seeds.
        var seeds = await db.WaySpeedSummaries
            .Where(s => wayIds.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        // Load all pending observations ordered oldest-first for sequential EMA application.
        var obs = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && wayIds.Contains(o.OsmWayId.Value))
            .OrderBy(o => o.Timestamp)
            .Select(o => new { WayId = o.OsmWayId!.Value, SpeedKmh = o.SpeedMs * 3.6, o.SpeedLimitKmh })
            .AsNoTracking()
            .ToListAsync(ct);

        if (obs.Count == 0) return;

        var wayIds_out  = new List<long>();
        var constrained = new List<double>();
        var freeflow    = new List<double>();
        var speedLimits = new List<double?>();

        foreach (var group in obs.GroupBy(o => o.WayId))
        {
            var wayId      = group.Key;
            var speedLimit = group.FirstOrDefault(o => o.SpeedLimitKmh != null)?.SpeedLimitKmh;

            seeds.TryGetValue(wayId, out var seed);

            // ConstrainedSpeed: sequential EMA over all observations, oldest first.
            double emaConstrained = seed?.ConstrainedSpeed ?? seed?.FreeflowSpeed ?? group.First().SpeedKmh;
            foreach (var o in group)
                emaConstrained = emaConstrained * (1 - EmaAlpha) + o.SpeedKmh * EmaAlpha;

            // FreeflowSpeed: sequential EMA of observations within 20% of speed limit
            // (i.e. not in congestion). Falls back to all observations when no limit is known.
            var freeflowObs = speedLimit is > 0
                ? group.Where(o => o.SpeedKmh >= speedLimit.Value * 0.8).ToList()
                : group.ToList();

            double emaFreeflow = seed?.FreeflowSpeed ?? (freeflowObs.Count > 0 ? freeflowObs[0].SpeedKmh : emaConstrained);
            foreach (var o in freeflowObs)
                emaFreeflow = emaFreeflow * (1 - EmaAlpha) + o.SpeedKmh * EmaAlpha;

            wayIds_out  .Add(wayId);
            constrained .Add(Math.Round(emaConstrained, 1));
            freeflow    .Add(Math.Round(emaFreeflow,    1));
            speedLimits .Add(speedLimit);
        }

        // Bulk upsert via unnest — one round-trip for all ways.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "WaySpeedSummaries"
                ("OsmWayId", "ConstrainedSpeed", "FreeflowSpeed", "SpeedLimitKmh", "LastUpdated")
            SELECT v.way_id, v.constrained, v.freeflow, v.speed_limit, NOW()
            FROM   unnest(@way_ids, @constrained, @freeflow, @speed_limits)
                   AS v(way_id, constrained, freeflow, speed_limit)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "ConstrainedSpeed" = EXCLUDED."ConstrainedSpeed",
                "FreeflowSpeed"    = EXCLUDED."FreeflowSpeed",
                "SpeedLimitKmh"    = COALESCE(EXCLUDED."SpeedLimitKmh", "WaySpeedSummaries"."SpeedLimitKmh"),
                "LastUpdated"      = NOW()
            """,
            [
                new NpgsqlParameter("way_ids",      NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = wayIds_out .ToArray() },
                new NpgsqlParameter("constrained",  NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = constrained.ToArray() },
                new NpgsqlParameter("freeflow",     NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = freeflow   .ToArray() },
                new NpgsqlParameter("speed_limits", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = speedLimits.ToArray() },
            ],
            ct);
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
            var constrained = summary is not null ? (int)Math.Round(summary.ConstrainedSpeed ?? summary.FreeflowSpeed) : freeflow;
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
