using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

public class TrafficAggregationService(AppDbContext db)
{
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
    /// fold their data into EdgeSpeedAggregate and H3SpeedAggregate, mark them aggregated,
    /// then delete observations that are no longer needed:
    ///   - Aggregated observations older than the live retention window (1 h)
    ///   - Unmatched observations older than 24 h (cannot be map-matched)
    ///
    /// Edge key: (OsmWayId, OsmStartNodeId, OsmEndNodeId, DayOfWeek, FiveMinuteBucket)
    /// H3 key:   (H3CellIndex, DayOfWeek, FiveMinuteBucket)
    /// </summary>
    public async Task ReaggregateAsync(CancellationToken ct = default)
    {
        // ── 1. Load only unprocessed matched observations ────────────────────
        var newlyMatched = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && o.OsmStartNodeId != null && o.H3CellIndex != null)
            .ToListAsync(ct);

        if (newlyMatched.Count > 0)
        {
            var edgeBuckets = new Dictionary<(long WayId, long StartNode, long EndNode, int Dow, int Bucket), (double Sum, int Count)>();
            var h3Buckets   = new Dictionary<(long Cell, int Dow, int Bucket), (double Sum, int Count)>();

            foreach (var obs in newlyMatched)
            {
                var ts     = obs.Timestamp.ToUniversalTime();
                var dow    = (int)ts.DayOfWeek;
                var bucket = ToFiveMinuteBucket(ts);
                var kmh    = obs.SpeedMs * 3.6;

                var eKey = (obs.OsmWayId!.Value, obs.OsmStartNodeId!.Value, obs.OsmEndNodeId ?? 0L, dow, bucket);
                edgeBuckets[eKey] = edgeBuckets.TryGetValue(eKey, out var ea)
                    ? (ea.Sum + kmh, ea.Count + 1) : (kmh, 1);

                var hKey = (obs.H3CellIndex!.Value, dow, bucket);
                h3Buckets[hKey] = h3Buckets.TryGetValue(hKey, out var ha)
                    ? (ha.Sum + kmh, ha.Count + 1) : (kmh, 1);
            }

            var now = DateTime.UtcNow;

            // ── 2. Increment edge aggregates (add new data to running sums) ──
            foreach (var (key, (sum, count)) in edgeBuckets)
            {
                var (wayId, startNode, endNode, dow, bucket) = key;
                var existing = await db.EdgeSpeedAggregates
                    .FirstOrDefaultAsync(a => a.OsmWayId == wayId
                        && a.OsmStartNodeId == startNode
                        && a.OsmEndNodeId == endNode
                        && a.DayOfWeek == dow
                        && a.FiveMinuteBucket == bucket, ct);

                if (existing is null)
                {
                    db.EdgeSpeedAggregates.Add(new EdgeSpeedAggregate
                    {
                        OsmWayId         = wayId,
                        OsmStartNodeId   = startNode,
                        OsmEndNodeId     = endNode,
                        DayOfWeek        = dow,
                        FiveMinuteBucket = bucket,
                        SumSpeedKmh      = sum,
                        SampleCount      = count,
                        LastUpdated      = now
                    });
                }
                else
                {
                    existing.SumSpeedKmh += sum;   // increment, not replace
                    existing.SampleCount += count;
                    existing.LastUpdated  = now;
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
                Latitude       = o.Latitude,
                Longitude      = o.Longitude,
                SpeedMs        = o.SpeedMs,
                SpeedKmh       = Math.Round(o.SpeedMs * 3.6, 1),
                Heading        = o.Heading,
                OsmWayId       = o.OsmWayId,
                OsmStartNodeId = o.OsmStartNodeId,
                OsmEndNodeId   = o.OsmEndNodeId,
                Timestamp      = o.Timestamp
            })
            .ToListAsync(ct);

        return new LiveTrafficResponse
        {
            Points      = obs,
            TotalPoints = obs.Count,
            AsOf        = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Live edge-level traffic CSV (rolling 30-minute window).
    /// Format: osm_way_id,start_node_id,end_node_id,avg_speed_kmh,sample_count
    /// </summary>
    public async Task WriteLiveEdgeCsvAsync(Stream output, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null && o.OsmStartNodeId != null)
            .Select(o => new { o.OsmWayId, o.OsmStartNodeId, o.OsmEndNodeId, o.SpeedMs })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,start_node_id,end_node_id,avg_speed_kmh,sample_count");

        foreach (var g in obs
            .GroupBy(o => (o.OsmWayId!.Value, o.OsmStartNodeId!.Value, o.OsmEndNodeId ?? 0L))
            .OrderBy(g => g.Key.Item1))
        {
            var avgKmh = g.Average(o => o.SpeedMs) * 3.6;
            await writer.WriteLineAsync(
                $"{g.Key.Item1},{g.Key.Item2},{g.Key.Item3},{avgKmh:F1},{g.Count()}");
        }
    }

    /// <summary>
    /// Historic edge traffic CSV from pre-aggregated 5-minute buckets.
    /// One row per (edge, time_slot). time = day_of_week * 288 + minute_of_day / 5, range 0–2015.
    /// Format: osm_way_id,start_node_id,end_node_id,speed,time
    ///   speed in km/h (rounded to 1 decimal); only slots with observed data are emitted.
    /// </summary>
    public async Task WriteHistoricEdgeCsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.EdgeSpeedAggregates
            .OrderBy(a => a.OsmWayId)
            .ThenBy(a => a.OsmStartNodeId)
            .ThenBy(a => a.OsmEndNodeId)
            .ThenBy(a => a.DayOfWeek)
            .ThenBy(a => a.FiveMinuteBucket)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,start_node_id,end_node_id,speed,time");

        foreach (var a in aggregates)
        {
            var speed = a.SampleCount > 0 ? a.SumSpeedKmh / a.SampleCount : 0;
            var time  = a.DayOfWeek * 288 + a.FiveMinuteBucket;
            await writer.WriteLineAsync($"{a.OsmWayId},{a.OsmStartNodeId},{a.OsmEndNodeId},{speed:F1},{time}");
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
