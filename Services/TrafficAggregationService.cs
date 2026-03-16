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
            // Pre-load existing way aggregates for all ways that have road-works observations,
            // so we can check whether the current aggregate is already below the 70% threshold.
            var roadWorksWayIds = newlyMatched
                .Where(o => o.RoadWorks && o.SpeedLimitKmh > 0)
                .Select(o => o.OsmWayId!.Value)
                .ToHashSet();

            Dictionary<(long WayId, int Dow, int Bucket), double> existingAvgByKey = [];
            Dictionary<(long WayId, int Dow, int Bucket), double?> existingLimitByKey = [];
            if (roadWorksWayIds.Count > 0)
            {
                var existing = await db.WaySpeedAggregates
                    .Where(a => roadWorksWayIds.Contains(a.OsmWayId) && a.SampleCount > 0)
                    .ToListAsync(ct);
                existingAvgByKey = existing.ToDictionary(
                    a => (a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket),
                    a => a.SumSpeedKmh / a.SampleCount);
                existingLimitByKey = existing.ToDictionary(
                    a => (a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket),
                    a => a.SpeedLimitKmh);
            }

            var wayBuckets = new Dictionary<(long WayId, int Dow, int Bucket), (double Sum, int Count, double? SpeedLimit)>();
            var h3Buckets   = new Dictionary<(long Cell, int Dow, int Bucket), (double Sum, int Count)>();

            foreach (var obs in newlyMatched)
            {
                var ts     = obs.Timestamp.ToUniversalTime();
                var dow    = (int)ts.DayOfWeek;
                var bucket = ToFiveMinuteBucket(ts);
                var kmh    = obs.SpeedMs * 3.6;

                // Road works: cap speed at 70% of the limit, but only when the current
                // aggregate is not already below that threshold (avoid inflating slow traffic).
                if (obs.RoadWorks && obs.SpeedLimitKmh > 0)
                {
                    var threshold = obs.SpeedLimitKmh.Value * 0.7;
                    var existingKey = (obs.OsmWayId!.Value, dow, bucket);
                    var currentAvg = existingAvgByKey.GetValueOrDefault(existingKey, double.MaxValue);
                    var currentSpeedLimit = existingLimitByKey.GetValueOrDefault(existingKey);
                    if(currentSpeedLimit != null)
                        if(currentSpeedLimit < obs.SpeedLimitKmh)
                            threshold = currentSpeedLimit.Value * 0.7;
                    if (currentAvg >= threshold)
                        kmh = threshold;
                }

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
                        LastUpdated      = now,
                        FirstSeenAt      = now
                    });
                }
                else
                {
                    existing.SumSpeedKmh  += sum;   // increment, not replace
                    existing.SampleCount  += count;
                    existing.SpeedLimitKmh = speedLimit ?? existing.SpeedLimitKmh;
                    existing.LastUpdated   = now;
                    // FirstSeenAt is intentionally never updated
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
                        LastUpdated      = now,
                        FirstSeenAt      = now
                    });
                }
                else
                {
                    existing.SumSpeedKmh += sum;
                    existing.SampleCount += count;
                    existing.LastUpdated  = now;
                    // FirstSeenAt is intentionally never updated
                }
            }

            // ── 4. Mark observations as aggregated ───────────────────────────
            foreach (var obs in newlyMatched)
                obs.IsAggregated = true;

            await db.SaveChangesAsync(ct);

            // ── 5. Rebuild WaySpeedSummary for affected ways ─────────────────
            var affectedWayIds = wayBuckets.Keys.Select(k => k.WayId).Distinct().ToList();
            await RebuildSummariesAsync(affectedWayIds, ct);
        }

        // ── 6. Refresh constrained (live) speeds in all summaries ────────────
        await RefreshConstrainedSpeedsAsync(ct);

        // ── 7. Delete observations outside the retention windows ─────────────
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

        // Load summaries to get per-way freeflow speed for blending
        var wayIdSet  = historicByWay.Keys.Union(liveByWay.Keys).ToList();
        var summaries = await db.WaySpeedSummaries
            .Where(s => wayIdSet.Contains(s.OsmWayId))
            .ToDictionaryAsync(s => s.OsmWayId, ct);

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
                var freeflow = summaries.TryGetValue(wayId, out var s) ? s.FreeflowSpeed : (hist!.SpeedLimitKmh ?? 0);
                speedKmh = BlendedAvg(hist!.SumSpeedKmh, hist.SampleCount, hist.FirstSeenAt, freeflow);
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

        var summaries = await db.WaySpeedSummaries
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,speed,time");

        foreach (var a in aggregates)
        {
            double speed;
            if (a.SampleCount > 0)
            {
                var freeflow = summaries.TryGetValue(a.OsmWayId, out var s)
                    ? s.FreeflowSpeed
                    : (a.SpeedLimitKmh ?? 0);
                speed = BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, freeflow);
            }
            else speed = 0;

            var time = a.DayOfWeek * 288 + a.FiveMinuteBucket;
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

        // Per-cell freeflow = 85th pct of raw bucket averages
        var freeflowByCell = aggregates
            .Where(a => a.SampleCount > 0)
            .GroupBy(a => a.H3CellIndex)
            .ToDictionary(
                g => g.Key,
                g => Percentile(g.Select(a => a.SumSpeedKmh / a.SampleCount).OrderBy(v => v).ToList(), 0.85));

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("h3_index,speed,time");

        foreach (var a in aggregates)
        {
            double speed;
            if (a.SampleCount > 0)
            {
                var freeflow = freeflowByCell.GetValueOrDefault(a.H3CellIndex, 0);
                speed = BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, freeflow);
            }
            else speed = 0;

            var time = a.DayOfWeek * 288 + a.FiveMinuteBucket;
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
        var allIds = historicByWay.Keys.Union(liveByWay.Keys).ToList();

        var summaries = await db.WaySpeedSummaries
            .Where(s => allIds.Contains(s.OsmWayId))
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

            if (hasLive)  { speedKmh = live.AvgKmh; source = "live"; }
            else
            {
                var freeflow = summaries.TryGetValue(wayId, out var s) ? s.FreeflowSpeed : (hist!.SpeedLimitKmh ?? 0);
                speedKmh = BlendedAvg(hist!.SumSpeedKmh, hist.SampleCount, hist.FirstSeenAt, freeflow);
                source = "historic";
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

            result[wayId] = new WayTrafficData(speedKmh, speedLimit, delayFactor, jamEstimate, source, liveSamples, historicSamples);
        }
        return result;
    }

    // ── WaySpeedSummary maintenance ─────────────────────────────────────────────

    /// <summary>
    /// Blends observed average speed with freeflow for unobserved weekly slots.
    /// If a (way, dow, bucket) has been "alive" for N weeks but only has M observations,
    /// the remaining (N - M) weeks are assumed to be freeflow.
    /// Returns the raw observed average when sampleCount >= totalSlots (dense data).
    /// </summary>
    private static double BlendedAvg(double sumSpeedKmh, int sampleCount, DateTime firstSeenAt, double freeflowSpeed)
    {
        var totalSlots = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - firstSeenAt).TotalDays / 7.0));
        var missing    = Math.Max(0, totalSlots - sampleCount);
        return (sumSpeedKmh + missing * freeflowSpeed) / (sampleCount + missing);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        var idx = p * (sorted.Count - 1);
        var lo  = (int)idx;
        var hi  = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    /// <summary>
    /// Rebuilds WaySpeedSummary rows for the given set of way IDs from the
    /// current WaySpeedAggregate data.  Freeflow = 85th percentile of bucket
    /// averages; P50/P95 = 50th / 95th percentiles.
    /// </summary>
    private async Task RebuildSummariesAsync(IReadOnlyList<long> wayIds, CancellationToken ct)
    {
        var allBuckets = await db.WaySpeedAggregates
            .Where(a => wayIds.Contains(a.OsmWayId) && a.SampleCount > 0)
            .Select(a => new { a.OsmWayId, a.SumSpeedKmh, a.SampleCount, a.SpeedLimitKmh, a.FirstSeenAt })
            .ToListAsync(ct);

        var byWay = allBuckets.GroupBy(a => a.OsmWayId);
        var now   = DateTime.UtcNow;

        foreach (var g in byWay)
        {
            // Freeflow = 85th pct of raw bucket averages (unblended — represents actual fast periods)
            var rawAvgs = g.Select(a => a.SumSpeedKmh / a.SampleCount)
                           .OrderBy(v => v)
                           .ToList();
            var totalSamples = g.Sum(a => a.SampleCount);
            var speedLimit   = g.FirstOrDefault(a => a.SpeedLimitKmh != null)?.SpeedLimitKmh;

            var freeflow = Percentile(rawAvgs, 0.85);
            // Freeflow must be at least 75% of the speed limit to avoid
            // unrealistically low uncongested baselines from sparse data.
            if (speedLimit is > 0)
                freeflow = Math.Max(freeflow, speedLimit.Value * 0.75);

            // P50/P95 use blended averages: unobserved weekly slots count as freeflow,
            // so a bucket that was congested 6 out of 52 weeks is weighted accordingly.
            var blendedAvgs = g.Select(a => BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, freeflow))
                               .OrderBy(v => v)
                               .ToList();
            var p50 = Percentile(blendedAvgs, 0.50);
            var p95 = Percentile(blendedAvgs, 0.95);

            var existing = await db.WaySpeedSummaries
                .FirstOrDefaultAsync(s => s.OsmWayId == g.Key, ct);

            if (existing is null)
            {
                db.WaySpeedSummaries.Add(new WaySpeedSummary
                {
                    OsmWayId      = g.Key,
                    FreeflowSpeed = Math.Round(freeflow, 1),
                    P50Speed      = Math.Round(p50, 1),
                    P95Speed      = Math.Round(p95, 1),
                    SpeedLimitKmh = speedLimit,
                    TotalSamples  = totalSamples,
                    LastUpdated   = now
                });
            }
            else
            {
                existing.FreeflowSpeed = Math.Round(freeflow, 1);
                existing.P50Speed      = Math.Round(p50, 1);
                existing.P95Speed      = Math.Round(p95, 1);
                existing.SpeedLimitKmh = speedLimit ?? existing.SpeedLimitKmh;
                existing.TotalSamples  = totalSamples;
                existing.LastUpdated   = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Updates ConstrainedSpeed on all WaySpeedSummary rows from the live
    /// 30-minute rolling window.  Clears it for ways with no recent data.
    /// </summary>
    private async Task RefreshConstrainedSpeedsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var now    = DateTime.UtcNow;

        var liveRaw = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null)
            .Select(o => new { o.OsmWayId, SpeedKmh = o.SpeedMs * 3.6 })
            .ToListAsync(ct);

        var liveByWay = liveRaw
            .GroupBy(o => o.OsmWayId!.Value)
            .ToDictionary(g => g.Key, g => g.Average(x => x.SpeedKmh));

        var summaries = await db.WaySpeedSummaries.ToListAsync(ct);
        foreach (var s in summaries)
        {
            s.ConstrainedSpeed = liveByWay.TryGetValue(s.OsmWayId, out var v)
                ? Math.Round(v, 1)
                : null;
            s.LastUpdated = now;
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Valhalla CSV export ─────────────────────────────────────────────────────

    /// <summary>
    /// Exports traffic data in Valhalla's predicted-traffic CSV format.
    /// Format: way_id,freeflow_speed,constrained_speed,historical_speeds
    ///   freeflow_speed   — 85th-percentile of bucket averages (integer km/h)
    ///   constrained_speed — live 30-min average if available, else P50 (integer km/h)
    ///   historical_speeds — 2016 comma-separated integer km/h values, one per
    ///                        5-minute slot (Mon 00:00 … Sun 23:55, Valhalla epoch).
    ///                        Slots without data use freeflow_speed as fill.
    /// </summary>
    public async Task WriteValhallaExportCsvAsync(Stream output, CancellationToken ct = default)
    {
        // Load all aggregate buckets
        var allBuckets = await db.WaySpeedAggregates
            .Where(a => a.SampleCount > 0)
            .Select(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket, a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt })
            .ToListAsync(ct);

        // Load summaries for freeflow / constrained
        var summaries = await db.WaySpeedSummaries
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        // Build slot lookup: wayId → (valhalla_slot → blended_avg_kmh)
        // Valhalla slot = ((dow + 6) % 7) * 288 + bucket  (Monday = day 0)
        // Blended avg weights unobserved weekly slots as freeflow so that a bucket
        // with jams in 6 out of 52 weeks doesn't look like permanent congestion.
        var bucketsByWay = allBuckets
            .GroupBy(b => b.OsmWayId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    summaries.TryGetValue(g.Key, out var sum);
                    var freeflow = sum?.FreeflowSpeed ?? 0;
                    return g.ToDictionary(
                        b => ((b.DayOfWeek + 6) % 7) * 288 + b.FiveMinuteBucket,
                        b => BlendedAvg(b.SumSpeedKmh, b.SampleCount, b.FirstSeenAt, freeflow));
                });

        var allWayIds = bucketsByWay.Keys.Union(summaries.Keys).Distinct().OrderBy(id => id);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("way_id,freeflow_speed,constrained_speed,historical_speeds");

        var speedBuffer = new float[2016];

        foreach (var wayId in allWayIds)
        {
            summaries.TryGetValue(wayId, out var summary);
            var freeflow    = summary is not null ? (int)Math.Round(summary.FreeflowSpeed) : 0;
            var constrained = summary is not null
                ? (int)Math.Round(summary.ConstrainedSpeed ?? summary.P50Speed)
                : freeflow;

            bucketsByWay.TryGetValue(wayId, out var slots);

            // Build 2016 float speed values (fill missing slots with freeflow)
            var fill = (float)freeflow;
            for (var i = 0; i < 2016; i++)
                speedBuffer[i] = slots is not null && slots.TryGetValue(i, out var sv)
                    ? (float)sv
                    : fill;

            var encoded = ValhallaSpeedEncoder.Encode(speedBuffer);
            await writer.WriteLineAsync($"{wayId},{freeflow},{constrained},{encoded}");
        }
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
