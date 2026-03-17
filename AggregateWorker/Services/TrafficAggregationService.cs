using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.AggregateWorker.Services;

public class TrafficAggregationService(AppDbContext db)
{
    private const int AggregateBatchSize = 5_000;

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

    public async Task ReaggregateAsync(CancellationToken ct = default)
    {
        // Process in bounded batches so we never load millions of rows at once.
        var newlyMatched = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && o.H3CellIndex != null)
            .OrderBy(o => o.Id)
            .Take(AggregateBatchSize)
            .ToListAsync(ct);

        if (newlyMatched.Count > 0)
        {
            var roadWorksWayIds = newlyMatched
                .Where(o => o.RoadWorks && o.SpeedLimitKmh > 0)
                .Select(o => o.OsmWayId!.Value)
                .ToHashSet();

            Dictionary<(long WayId, int Dow, int Bucket), double>  existingAvgByKey   = [];
            Dictionary<(long WayId, int Dow, int Bucket), double?> existingLimitByKey = [];
            if (roadWorksWayIds.Count > 0)
            {
                var existing = await db.WaySpeedAggregates
                    .Where(a => roadWorksWayIds.Contains(a.OsmWayId) && a.SampleCount > 0)
                    .AsNoTracking()
                    .ToListAsync(ct);
                existingAvgByKey   = existing.ToDictionary(a => (a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket), a => a.SumSpeedKmh / a.SampleCount);
                existingLimitByKey = existing.ToDictionary(a => (a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket), a => a.SpeedLimitKmh);
            }

            var wayBuckets = new Dictionary<(long WayId, int Dow, int Bucket), (double Sum, int Count, double? SpeedLimit)>();
            var h3Buckets  = new Dictionary<(long Cell, int Dow, int Bucket), (double Sum, int Count)>();

            foreach (var obs in newlyMatched)
            {
                var ts     = obs.Timestamp.ToUniversalTime();
                var dow    = (int)ts.DayOfWeek;
                var bucket = ToFiveMinuteBucket(ts);
                var kmh    = obs.SpeedMs * 3.6;

                if (obs.RoadWorks && obs.SpeedLimitKmh > 0)
                {
                    var threshold = obs.SpeedLimitKmh.Value * 0.7;
                    var existingKey = (obs.OsmWayId!.Value, dow, bucket);
                    var currentAvg = existingAvgByKey.GetValueOrDefault(existingKey, double.MaxValue);
                    var currentSpeedLimit = existingLimitByKey.GetValueOrDefault(existingKey);
                    if (currentSpeedLimit != null && currentSpeedLimit < obs.SpeedLimitKmh)
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

            // ── 2. Bulk-load existing way aggregates, then upsert in memory ──
            var affectedWayIds = wayBuckets.Keys.Select(k => k.WayId).ToHashSet();
            var existingWayAggs = await db.WaySpeedAggregates
                .Where(a => affectedWayIds.Contains(a.OsmWayId))
                .ToDictionaryAsync(a => (a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket), ct);

            foreach (var (key, (sum, count, speedLimit)) in wayBuckets)
            {
                var (wayId, dow, bucket) = key;
                if (existingWayAggs.TryGetValue(key, out var existing))
                {
                    existing.SumSpeedKmh  += sum;
                    existing.SampleCount  += count;
                    existing.SpeedLimitKmh = speedLimit ?? existing.SpeedLimitKmh;
                    existing.LastUpdated   = now;
                }
                else
                {
                    var row = new WaySpeedAggregate
                    {
                        OsmWayId         = wayId,
                        DayOfWeek        = dow,
                        FiveMinuteBucket = bucket,
                        SumSpeedKmh      = sum,
                        SampleCount      = count,
                        SpeedLimitKmh    = speedLimit,
                        LastUpdated      = now,
                        FirstSeenAt      = now
                    };
                    db.WaySpeedAggregates.Add(row);
                    existingWayAggs[key] = row;
                }
            }

            // ── 3. Bulk-load existing H3 aggregates, then upsert in memory ──
            var affectedCells = h3Buckets.Keys.Select(k => k.Cell).ToHashSet();
            var existingH3Aggs = await db.H3SpeedAggregates
                .Where(a => affectedCells.Contains(a.H3CellIndex))
                .ToDictionaryAsync(a => (a.H3CellIndex, a.DayOfWeek, a.FiveMinuteBucket), ct);

            foreach (var (key, (sum, count)) in h3Buckets)
            {
                var (cell, dow, bucket) = key;
                if (existingH3Aggs.TryGetValue(key, out var existing))
                {
                    existing.SumSpeedKmh += sum;
                    existing.SampleCount += count;
                    existing.LastUpdated  = now;
                }
                else
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
            }

            // ── 4. Mark observations as aggregated ───────────────────────────
            foreach (var obs in newlyMatched)
                obs.IsAggregated = true;

            await db.SaveChangesAsync(ct);

            // ── 5. Rebuild summaries for affected ways ────────────────────────
            await RebuildSummariesAsync(affectedWayIds.ToList(), ct);
        }

        // ── 6. Refresh constrained speeds via a single SQL UPDATE ─────────────
        await RefreshConstrainedSpeedsAsync(ct);

        // ── 7. Delete observations outside the retention windows ──────────────
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
        var aggregates = await db.WaySpeedAggregates
            .OrderBy(a => a.OsmWayId).ThenBy(a => a.DayOfWeek).ThenBy(a => a.FiveMinuteBucket)
            .AsNoTracking()
            .ToListAsync(ct);

        var summaries = await db.WaySpeedSummaries
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,speed,time");

        foreach (var a in aggregates)
        {
            var speed = a.SampleCount > 0
                ? BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, summaries.TryGetValue(a.OsmWayId, out var s) ? s.FreeflowSpeed : (a.SpeedLimitKmh ?? 0))
                : 0.0;
            await writer.WriteLineAsync($"{a.OsmWayId},{speed:F1},{a.DayOfWeek * 288 + a.FiveMinuteBucket}");
        }
    }

    public async Task WriteHistoricH3CsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.H3SpeedAggregates
            .OrderBy(a => a.H3CellIndex).ThenBy(a => a.DayOfWeek).ThenBy(a => a.FiveMinuteBucket)
            .AsNoTracking()
            .ToListAsync(ct);

        var freeflowByCell = aggregates
            .Where(a => a.SampleCount > 0)
            .GroupBy(a => a.H3CellIndex)
            .ToDictionary(g => g.Key, g => Percentile(g.Select(a => a.SumSpeedKmh / a.SampleCount).OrderBy(v => v).ToList(), 0.85));

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("h3_index,speed,time");

        foreach (var a in aggregates)
        {
            var speed = a.SampleCount > 0
                ? BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, freeflowByCell.GetValueOrDefault(a.H3CellIndex, 0))
                : 0.0;
            await writer.WriteLineAsync($"{(ulong)a.H3CellIndex:x},{speed:F1},{a.DayOfWeek * 288 + a.FiveMinuteBucket}");
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

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        var idx = p * (sorted.Count - 1);
        var lo  = (int)idx;
        var hi  = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    private async Task RebuildSummariesAsync(IReadOnlyList<long> wayIds, CancellationToken ct)
    {
        var allBuckets = await db.WaySpeedAggregates
            .Where(a => wayIds.Contains(a.OsmWayId) && a.SampleCount > 0)
            .AsNoTracking()
            .Select(a => new { a.OsmWayId, a.SumSpeedKmh, a.SampleCount, a.SpeedLimitKmh, a.FirstSeenAt })
            .ToListAsync(ct);

        // Pre-load all existing summaries in one query
        var existingSummaries = await db.WaySpeedSummaries
            .Where(s => wayIds.Contains(s.OsmWayId))
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var byWay = allBuckets.GroupBy(a => a.OsmWayId);
        var now   = DateTime.UtcNow;

        foreach (var g in byWay)
        {
            var rawAvgs      = g.Select(a => a.SumSpeedKmh / a.SampleCount).OrderBy(v => v).ToList();
            var totalSamples = g.Sum(a => a.SampleCount);
            var speedLimit   = g.FirstOrDefault(a => a.SpeedLimitKmh != null)?.SpeedLimitKmh;

            var freeflow = Percentile(rawAvgs, 0.85);
            if (speedLimit is > 0)
                freeflow = Math.Max(freeflow, speedLimit.Value * 0.75);

            var blendedAvgs = g.Select(a => BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, freeflow)).OrderBy(v => v).ToList();
            var p50 = Percentile(blendedAvgs, 0.50);
            var p95 = Percentile(blendedAvgs, 0.95);

            if (existingSummaries.TryGetValue(g.Key, out var existing))
            {
                existing.FreeflowSpeed = Math.Round(freeflow, 1);
                existing.P50Speed      = Math.Round(p50, 1);
                existing.P95Speed      = Math.Round(p95, 1);
                existing.SpeedLimitKmh = speedLimit ?? existing.SpeedLimitKmh;
                existing.TotalSamples  = totalSamples;
                existing.LastUpdated   = now;
            }
            else
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
        }

        await db.SaveChangesAsync(ct);
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

        // Clear constrained speed for ways with no recent live data
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "WaySpeedSummaries"
            SET    "ConstrainedSpeed" = NULL,
                   "LastUpdated"      = NOW()
            WHERE  "ConstrainedSpeed" IS NOT NULL
              AND  "OsmWayId" NOT IN (
                       SELECT DISTINCT "OsmWayId"
                       FROM   "SpeedObservations"
                       WHERE  "Timestamp" >= NOW() - INTERVAL '30 minutes'
                         AND  "OsmWayId"  IS NOT NULL
                   )
            """, ct);
    }

    // ── Valhalla CSV export ─────────────────────────────────────────────────────

    public async Task WriteValhallaExportCsvAsync(Stream output, CancellationToken ct = default)
    {
        var allBuckets = await db.WaySpeedAggregates
            .Where(a => a.SampleCount > 0)
            .AsNoTracking()
            .Select(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket, a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt })
            .ToListAsync(ct);

        var summaries = await db.WaySpeedSummaries
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var bucketsByWay = allBuckets
            .GroupBy(b => b.OsmWayId)
            .ToDictionary(g => g.Key, g =>
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
            var constrained = summary is not null ? (int)Math.Round(summary.ConstrainedSpeed ?? summary.P50Speed) : freeflow;

            bucketsByWay.TryGetValue(wayId, out var slots);
            var fill = (float)freeflow;
            for (var i = 0; i < 2016; i++)
                speedBuffer[i] = slots is not null && slots.TryGetValue(i, out var sv) ? (float)sv : fill;

            await writer.WriteLineAsync($"{wayId},{freeflow},{constrained},{ValhallaSpeedEncoder.Encode(speedBuffer)}");
        }
    }

    public async Task WriteLiveH3CsvAsync(Stream output, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.H3CellIndex != null)
            .AsNoTracking()
            .Select(o => new { o.H3CellIndex, o.SpeedMs })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("h3_index,avg_speed_kmh,sample_count");

        foreach (var g in obs.GroupBy(o => o.H3CellIndex!.Value).OrderBy(g => g.Key))
            await writer.WriteLineAsync($"{(ulong)g.Key:x},{g.Average(o => o.SpeedMs) * 3.6:F1},{g.Count()}");
    }
}
