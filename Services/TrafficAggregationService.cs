using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

public class TrafficAggregationService(AppDbContext db)
{
    // ── Traffic jam estimation ───────────────────────────────────────────────────

    /// <summary>
    /// Piecewise-linear mapping from delay factor to jam severity.
    ///   0.0  → 0.0  (free flow)
    ///   0.6  → 0.7  (stop-and-go onset)
    ///   0.85 → 1.0  (standing traffic)
    /// </summary>
    private static double ComputeJam(double delay) => delay switch
    {
        <= 0    => 0.0,
        <= 0.6  => delay * (0.7 / 0.6),
        <= 0.85 => 0.7 + (delay - 0.6) * (0.3 / 0.25),
        _       => 1.0
    };

    // ── Time-bucket helpers ─────────────────────────────────────────────────────

    private static int ToFiveMinuteBucket(DateTime utc)
        => (utc.Hour * 60 + utc.Minute) / 5;

    // Observations are kept for at least this long so the live endpoint can still see them.
    private static readonly TimeSpan LiveRetention = TimeSpan.FromHours(1);
    // Unmatched observations older than this are unresolvable and can be dropped.
    private static readonly TimeSpan UnmatchedRetention = TimeSpan.FromHours(24);

    // ── Hybrid aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Incremental aggregation: process only newly matched (not yet aggregated) observations,
    /// fold their data into WaySpeedAggregate and H3SpeedAggregate, mark them aggregated,
    /// then delete observations that are no longer needed:
    ///   - Aggregated observations older than the live retention window (1 h)
    ///   - Unmatched observations older than 24 h (cannot be map-matched)
    ///
    /// Way key: (OsmWayId, OsmStartNodeId, OsmEndNodeId, DayOfWeek, FiveMinuteBucket)
    /// H3 key:   (H3CellIndex, DayOfWeek, FiveMinuteBucket)
    /// </summary>
    public async Task ReaggregateAsync(CancellationToken ct = default)
    {
        // ── 1. Load only unprocessed matched observations ────────────────────
        var newlyMatched = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && o.H3CellIndex != null)
            .ToListAsync(ct);

        if (newlyMatched.Count > 0)
        {
            var wayBuckets = new Dictionary<(long WayId, int Dow, int Bucket), (double Sum, int Count, double? SpeedLimit)>();
            var h3Buckets   = new Dictionary<(long Cell, int Dow, int Bucket), (double Sum, int Count)>();

            foreach (var obs in newlyMatched)
            {
                var ts     = obs.Timestamp.ToUniversalTime();
                var dow    = (int)ts.DayOfWeek;
                var bucket = ToFiveMinuteBucket(ts);
                var kmh    = obs.SpeedMs * 3.6;

                var eKey = (obs.OsmWayId!.Value, dow, bucket);
                wayBuckets[eKey] = wayBuckets.TryGetValue(eKey, out var ea)
                    ? (ea.Sum + kmh, ea.Count + 1, obs.SpeedLimitKmh ?? ea.SpeedLimit)
                    : (kmh, 1, obs.SpeedLimitKmh);

                var hKey = (obs.H3CellIndex!.Value, dow, bucket);
                h3Buckets[hKey] = h3Buckets.TryGetValue(hKey, out var ha)
                    ? (ha.Sum + kmh, ha.Count + 1) : (kmh, 1);
            }

            var now = DateTime.UtcNow;

            // ── 2. Increment way aggregates (add new data to running sums) ──
            foreach (var (key, (sum, count, speedLimit)) in wayBuckets)
            {
                var (wayId, dow, bucket) = key;
                var existing = await db.WaySpeedAggregates
                    .FirstOrDefaultAsync(a => a.OsmWayId == wayId
                        && a.DayOfWeek == dow
                        && a.FiveMinuteBucket == bucket, ct);

                if (existing is null)
                {
                    db.WaySpeedAggregates.Add(new WaySpeedAggregate
                    {
                        OsmWayId         = wayId,
                        DayOfWeek        = dow,
                        FiveMinuteBucket = bucket,
                        SumSpeedKmh      = sum,
                        SampleCount      = count,
                        SpeedLimitKmh    = speedLimit,
                        LastUpdated      = now
                    });
                }
                else
                {
                    existing.SumSpeedKmh  += sum;   // increment, not replace
                    existing.SampleCount  += count;
                    existing.SpeedLimitKmh = speedLimit ?? existing.SpeedLimitKmh;
                    existing.LastUpdated   = now;
                }
            }

            // ── 3. Increment H3 aggregates ───────────────────────────────────
            foreach (var (key, (sum, count)) in h3Buckets)
            {
                var (cell, dow, bucket) = key;
                var existing = await db.H3SpeedAggregates
                    .FirstOrDefaultAsync(a => a.H3CellIndex == cell
                        && a.DayOfWeek == dow
                        && a.FiveMinuteBucket == bucket, ct);

                if (existing is null)
                {
                    db.H3SpeedAggregates.Add(new H3SpeedAggregate
                    {
                        H3CellIndex      = cell,
                        DayOfWeek        = dow,
                        FiveMinuteBucket = bucket,
                        SumSpeedKmh      = sum,
                        SampleCount      = count,
                        LastUpdated      = now
                    });
                }
                else
                {
                    existing.SumSpeedKmh += sum;
                    existing.SampleCount += count;
                    existing.LastUpdated  = now;
                }
            }

            // ── 4. Mark observations as aggregated ───────────────────────────
            foreach (var obs in newlyMatched)
                obs.IsAggregated = true;

            await db.SaveChangesAsync(ct);
        }

        // ── 5. Delete observations outside the retention windows ─────────────
        var liveWindow      = DateTime.UtcNow - LiveRetention;
        var unmatchedCutoff = DateTime.UtcNow - UnmatchedRetention;

        await db.SpeedObservations
            .Where(o => (o.IsAggregated && o.CreatedAt < liveWindow)
                     || (o.OsmWayId == null && o.CreatedAt < unmatchedCutoff))
            .ExecuteDeleteAsync(ct);
    }

    // ── Live traffic (rolling 30-minute window) ─────────────────────────────────

    /// <summary>
    /// Get recent speed observations within a bounding box for live traffic display.
    /// </summary>
    public async Task<LiveTrafficResponse> GetLiveSpeedsAsync(LiveTrafficQuery query, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-query.MaxAgeMinutes);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff
                && o.Latitude >= query.MinLat && o.Latitude <= query.MaxLat
                && o.Longitude >= query.MinLon && o.Longitude <= query.MaxLon)
            .OrderByDescending(o => o.Timestamp)
            .Take(10_000)
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
                pt.DelayFactor  = Math.Round(delay, 3);
                pt.JamEstimate  = Math.Round(ComputeJam(delay), 3);
            }
        }

        return new LiveTrafficResponse
        {
            Points      = obs,
            TotalPoints = obs.Count,
            AsOf        = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Current per-way traffic as CSV: live observations (last 30 min) take priority;
    /// ways without live data fall back to the historic baseline for the matching
    /// day-of-week / 5-minute bucket.
    /// Format: osm_way_id,speed_kmh,speed_limit_kmh,delay_factor,jam_estimate,source,live_samples,historic_samples
    /// </summary>
    public async Task WriteCurrentWayCsvAsync(Stream output, CancellationToken ct = default)
    {
        var now    = DateTime.UtcNow;
        var dow    = (int)now.DayOfWeek;
        var bucket = ToFiveMinuteBucket(now);
        var cutoff = now.AddMinutes(-30);

        // Historic baseline for the current time slot
        var historic = await db.WaySpeedAggregates
            .Where(a => a.DayOfWeek == dow && a.FiveMinuteBucket == bucket)
            .ToListAsync(ct);

        // Live observations grouped by way
        var liveRaw = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null)
            .Select(o => new { o.OsmWayId, SpeedKmh = o.SpeedMs * 3.6, o.SpeedLimitKmh })
            .ToListAsync(ct);

        var liveByWay = liveRaw
            .GroupBy(o => o.OsmWayId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (
                    AvgKmh:      g.Average(x => x.SpeedKmh),
                    Count:       g.Count(),
                    SpeedLimit:  g.FirstOrDefault(x => x.SpeedLimitKmh != null)?.SpeedLimitKmh
                ));

        var historicByWay = historic.ToDictionary(a => a.OsmWayId);

        // Merge: union of all way IDs seen in either source
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

            if (hasLive)
            {
                speedKmh = live.AvgKmh;
                source   = "live";
            }
            else
            {
                speedKmh = hist!.SumSpeedKmh / hist.SampleCount;
                source   = "historic";
            }

            speedKmh = Math.Round(speedKmh, 1);

            var speedLimit = (hasLive ? live.SpeedLimit : null)
                          ?? (hasHistoric ? hist!.SpeedLimitKmh : null);

            double? delayFactor = null, jamEstimate = null;
            if (speedLimit is > 0)
            {
                var delay = Math.Clamp(1.0 - speedKmh / speedLimit.Value, 0.0, 1.0);
                delayFactor = Math.Round(delay, 3);
                jamEstimate = Math.Round(ComputeJam(delay), 3);
            }

            await writer.WriteLineAsync(
                $"{wayId},{speedKmh:F1},{speedLimit?.ToString("F1") ?? ""},{delayFactor?.ToString("F3") ?? ""},{jamEstimate?.ToString("F3") ?? ""},{source},{liveSamples},{historicSamples}");
        }
    }

    /// <summary>
    /// Live way-level traffic CSV (rolling 30-minute window).
    /// Format: osm_way_id,avg_speed_kmh,sample_count
    /// </summary>
    public async Task WriteLiveWayCsvAsync(Stream output, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null)
            .Select(o => new { o.OsmWayId, o.SpeedMs })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,avg_speed_kmh,sample_count");

        foreach (var g in obs.GroupBy(o => o.OsmWayId!.Value).OrderBy(g => g.Key))
        {
            var avgKmh = g.Average(o => o.SpeedMs) * 3.6;
            await writer.WriteLineAsync($"{g.Key},{avgKmh:F1},{g.Count()}");
        }
    }

    /// <summary>
    /// Historic way traffic CSV from pre-aggregated 5-minute buckets.
    /// One row per (way, time_slot). time = day_of_week * 288 + minute_of_day / 5, range 0–2015.
    /// Format: osm_way_id,speed,time
    ///   speed in km/h (rounded to 1 decimal); only slots with observed data are emitted.
    /// </summary>
    public async Task WriteHistoricWayCsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.WaySpeedAggregates
            .OrderBy(a => a.OsmWayId)
            .ThenBy(a => a.DayOfWeek)
            .ThenBy(a => a.FiveMinuteBucket)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,speed,time");

        foreach (var a in aggregates)
        {
            var speed = a.SampleCount > 0 ? a.SumSpeedKmh / a.SampleCount : 0;
            var time  = a.DayOfWeek * 288 + a.FiveMinuteBucket;
            await writer.WriteLineAsync($"{a.OsmWayId},{speed:F1},{time}");
        }
    }

    /// <summary>
    /// Historic H3-grid traffic CSV from pre-aggregated 5-minute buckets.
    /// One row per (h3_index, time_slot). time = day_of_week * 288 + minute_of_day / 5, range 0–2015.
    /// Format: h3_index,speed,time
    ///   h3_index as lowercase hex string; speed in km/h.
    /// </summary>
    public async Task WriteHistoricH3CsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.H3SpeedAggregates
            .OrderBy(a => a.H3CellIndex)
            .ThenBy(a => a.DayOfWeek)
            .ThenBy(a => a.FiveMinuteBucket)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("h3_index,speed,time");

        foreach (var a in aggregates)
        {
            var speed = a.SampleCount > 0 ? a.SumSpeedKmh / a.SampleCount : 0;
            var time  = a.DayOfWeek * 288 + a.FiveMinuteBucket;
            await writer.WriteLineAsync($"{(ulong)a.H3CellIndex:x},{speed:F1},{time}");
        }
    }

    /// <summary>
    /// Returns current traffic data keyed by OSM way ID for a specific set of ways.
    /// Live observations (last 30 min) take priority over the historic baseline.
    /// Used by tile generation.
    /// </summary>
    public async Task<Dictionary<long, WayTrafficData>> GetCurrentWayTrafficAsync(
        IEnumerable<long> wayIds,
        CancellationToken ct = default)
    {
        var ids = wayIds.ToList();
        if (ids.Count == 0) return [];

        var now    = DateTime.UtcNow;
        var dow    = (int)now.DayOfWeek;
        var bucket = ToFiveMinuteBucket(now);
        var cutoff = now.AddMinutes(-30);

        var historic = await db.WaySpeedAggregates
            .Where(a => ids.Contains(a.OsmWayId) && a.DayOfWeek == dow && a.FiveMinuteBucket == bucket)
            .ToListAsync(ct);

        var liveRaw = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null && ids.Contains(o.OsmWayId!.Value))
            .Select(o => new { o.OsmWayId, SpeedKmh = o.SpeedMs * 3.6, o.SpeedLimitKmh })
            .ToListAsync(ct);

        var liveByWay = liveRaw
            .GroupBy(o => o.OsmWayId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (
                    AvgKmh:     g.Average(x => x.SpeedKmh),
                    Count:      g.Count(),
                    SpeedLimit: g.FirstOrDefault(x => x.SpeedLimitKmh != null)?.SpeedLimitKmh
                ));

        var historicByWay = historic.ToDictionary(a => a.OsmWayId);
        var allIds = historicByWay.Keys.Union(liveByWay.Keys);

        var result = new Dictionary<long, WayTrafficData>();
        foreach (var wayId in allIds)
        {
            var hasLive     = liveByWay.TryGetValue(wayId, out var live);
            var hasHistoric = historicByWay.TryGetValue(wayId, out var hist);

            double speedKmh;
            string source;
            int liveSamples     = hasLive ? live.Count : 0;
            int historicSamples = hasHistoric && hist!.SampleCount > 0 ? hist.SampleCount : 0;

            if (hasLive)  { speedKmh = live.AvgKmh;                       source = "live"; }
            else          { speedKmh = hist!.SumSpeedKmh / hist.SampleCount; source = "historic"; }

            speedKmh = Math.Round(speedKmh, 1);
            var speedLimit = (hasLive ? live.SpeedLimit : null)
                          ?? (hasHistoric ? hist!.SpeedLimitKmh : null);

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

    /// <summary>
    /// Live H3-grid traffic CSV (rolling 30-minute window, resolution 9 ~174 m).
    /// Format: h3_index,avg_speed_kmh,sample_count
    ///   h3_index as lowercase hex string (e.g. 89283082803ffff)
    /// </summary>
    public async Task WriteLiveH3CsvAsync(Stream output, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.H3CellIndex != null)
            .Select(o => new { o.H3CellIndex, o.SpeedMs })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("h3_index,avg_speed_kmh,sample_count");

        foreach (var g in obs.GroupBy(o => o.H3CellIndex!.Value).OrderBy(g => g.Key))
        {
            var avgKmh = g.Average(o => o.SpeedMs) * 3.6;
            await writer.WriteLineAsync($"{((ulong)g.Key):x},{avgKmh:F1},{g.Count()}");
        }
    }
}
