using System.Formats.Tar;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

public class TrafficAggregationService(AppDbContext db)
{
    // ── Valhalla GraphId helpers (for predicted CSV export) ─────────────────────

    // Valhalla GraphId bit layout (64-bit value):
    //   bits  0-20: id      (21 bits)
    //   bits 21-42: tile_id (22 bits)
    //   bits 43-45: level   (3 bits)

    private static (uint Level, uint TileId, uint Id) DecodeGraphId(long graphId)
    {
        var v = (ulong)graphId;
        var id     = (uint)(v & 0x1FFFFF);
        var tileId = (uint)((v >> 21) & 0x3FFFFF);
        var level  = (uint)((v >> 43) & 0x7);
        return (level, tileId, id);
    }

    /// <summary>Returns "level/tile_id/id" — the edge_id string Valhalla uses in CSV files.</summary>
    public static string GraphIdToEdgeString(long graphId)
    {
        var (level, tileId, id) = DecodeGraphId(graphId);
        return $"{level}/{tileId}/{id}";
    }

    /// <summary>
    /// Returns the relative tile path without extension, e.g. "1/047/701".
    /// Matches Valhalla's GraphTile::FileSuffix convention (tile_id split at thousands).
    /// </summary>
    public static string GraphIdToTilePath(long graphId)
    {
        var (level, tileId, _) = DecodeGraphId(graphId);
        return $"{level}/{tileId / 1000:D3}/{tileId % 1000:D3}";
    }

    // ── Time-bucket helpers ─────────────────────────────────────────────────────

    private static int ToFiveMinuteBucket(DateTime utc)
        => (utc.Hour * 60 + utc.Minute) / 5;

    // Freeflow = night-time (midnight–5 AM, 10 PM–midnight)
    private static bool IsFreefowBucket(int bucket) => bucket < 60 || bucket >= 264;
    // Constrained = daytime (7 AM–7 PM)
    private static bool IsConstrainedBucket(int bucket) => bucket is >= 84 and <= 228;

    // ── Hybrid aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Recompute both edge-level and H3-level aggregates from all matched observations.
    /// Called by the background service every 5 minutes.
    ///
    /// Edge aggregates key: (OsmWayId, IsForward, DayOfWeek, FiveMinuteBucket)
    /// H3 aggregates key:   (H3CellIndex, DayOfWeek, FiveMinuteBucket)
    /// </summary>
    public async Task ReaggregateAsync(CancellationToken ct = default)
    {
        var matched = await db.SpeedObservations
            .Where(o => o.OsmWayId != null && o.H3CellIndex != null)
            .Select(o => new
            {
                o.OsmWayId,
                o.IsForward,
                o.H3CellIndex,
                o.Timestamp,
                o.SpeedMs
            })
            .ToListAsync(ct);

        if (matched.Count == 0) return;

        // ── Edge aggregation ─────────────────────────────────────────────────
        var edgeBuckets = new Dictionary<(long WayId, bool Forward, int Dow, int Bucket),
                                         (double Sum, int Count)>();

        // ── H3 aggregation ───────────────────────────────────────────────────
        var h3Buckets = new Dictionary<(long Cell, int Dow, int Bucket),
                                       (double Sum, int Count)>();

        foreach (var obs in matched)
        {
            var ts     = obs.Timestamp.ToUniversalTime();
            var dow    = (int)ts.DayOfWeek;
            var bucket = ToFiveMinuteBucket(ts);
            var kmh    = obs.SpeedMs * 3.6;

            var eKey = (obs.OsmWayId!.Value, obs.IsForward ?? true, dow, bucket);
            edgeBuckets[eKey] = edgeBuckets.TryGetValue(eKey, out var ea)
                ? (ea.Sum + kmh, ea.Count + 1)
                : (kmh, 1);

            var hKey = (obs.H3CellIndex!.Value, dow, bucket);
            h3Buckets[hKey] = h3Buckets.TryGetValue(hKey, out var ha)
                ? (ha.Sum + kmh, ha.Count + 1)
                : (kmh, 1);
        }

        var now = DateTime.UtcNow;

        // ── Upsert edge aggregates ───────────────────────────────────────────
        foreach (var (key, (sum, count)) in edgeBuckets)
        {
            var (wayId, forward, dow, bucket) = key;
            var existing = await db.EdgeSpeedAggregates
                .FirstOrDefaultAsync(a => a.OsmWayId == wayId
                    && a.IsForward == forward
                    && a.DayOfWeek == dow
                    && a.FiveMinuteBucket == bucket, ct);

            if (existing is null)
            {
                db.EdgeSpeedAggregates.Add(new EdgeSpeedAggregate
                {
                    OsmWayId        = wayId,
                    IsForward       = forward,
                    DayOfWeek       = dow,
                    FiveMinuteBucket = bucket,
                    SumSpeedKmh     = sum,
                    SampleCount     = count,
                    LastUpdated     = now
                });
            }
            else
            {
                existing.SumSpeedKmh  = sum;
                existing.SampleCount  = count;
                existing.LastUpdated  = now;
            }
        }

        // Refresh cached ValhallaEdgeId on edge aggregates from the most recent observation
        var vhallaLookup = await db.SpeedObservations
            .Where(o => o.OsmWayId != null)
            .GroupBy(o => new { o.OsmWayId, o.IsForward })
            .Select(g => new
            {
                g.Key.OsmWayId,
                g.Key.IsForward,
                // placeholder — we store ValhallaEdgeId in the observation
                // We do this via a join instead (see below)
            })
            .ToListAsync(ct);
        // Simpler: pull the most recent ValhallaEdgeId per (OsmWayId, IsForward) from
        // matched observations that had a Valhalla edge id stored by the old path.
        // Because new observations no longer carry ValhallaEdgeId in SpeedObservation,
        // the cache is populated only during map-matching (ProcessUnmatchedAsync is the
        // correct place; we skip the extra query here and just save).

        // ── Upsert H3 aggregates ─────────────────────────────────────────────
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
                existing.SumSpeedKmh  = sum;
                existing.SampleCount  = count;
                existing.LastUpdated  = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Valhalla predicted traffic export ───────────────────────────────────────

    /// <summary>
    /// Export predicted traffic speeds in Valhalla CSV format (single flat file).
    /// Only rows where ValhallaEdgeId is cached are included.
    /// Format: edge_id,freeflow_speed,constrained_speed
    /// </summary>
    public async Task WritePredictedCsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.EdgeSpeedAggregates
            .Where(a => a.ValhallaEdgeId != null)
            .OrderBy(a => a.ValhallaEdgeId)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);

        foreach (var edge in aggregates.GroupBy(a => a.ValhallaEdgeId!.Value))
        {
            ComputeSpeeds(edge, out var freeflow, out var constrained);
            if (freeflow <= 0 && constrained <= 0) continue;

            await writer.WriteLineAsync(
                $"{GraphIdToEdgeString(edge.Key)},{(int)Math.Round(freeflow)},{(int)Math.Round(constrained)}".AsMemory(), ct);
        }
    }

    /// <summary>
    /// Build a tar archive with one CSV per Valhalla tile for valhalla_add_predicted_traffic.
    /// Only edges with a cached ValhallaEdgeId are included.
    /// </summary>
    public async Task WriteTrafficTarAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.EdgeSpeedAggregates
            .Where(a => a.ValhallaEdgeId != null)
            .OrderBy(a => a.ValhallaEdgeId)
            .ToListAsync(ct);

        var byTile = aggregates
            .GroupBy(a => GraphIdToTilePath(a.ValhallaEdgeId!.Value))
            .OrderBy(g => g.Key);

        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);

        foreach (var tile in byTile)
        {
            var csv = new StringBuilder();
            foreach (var edge in tile.GroupBy(a => a.ValhallaEdgeId!.Value))
            {
                ComputeSpeeds(edge, out var freeflow, out var constrained);
                if (freeflow <= 0 && constrained <= 0) continue;
                csv.AppendLine($"{GraphIdToEdgeString(edge.Key)},{(int)Math.Round(freeflow)},{(int)Math.Round(constrained)}");
            }
            if (csv.Length == 0) continue;

            var csvBytes = Encoding.UTF8.GetBytes(csv.ToString());
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{tile.Key}.csv")
            {
                DataStream = new MemoryStream(csvBytes)
            };
            await tar.WriteEntryAsync(entry, ct);
        }
    }

    private static void ComputeSpeeds(IGrouping<long, EdgeSpeedAggregate> edge,
        out double freeflow, out double constrained)
    {
        double EdgeAvg(EdgeSpeedAggregate a) => a.SampleCount > 0 ? a.SumSpeedKmh / a.SampleCount : 0;
        var ff  = edge.Where(a => IsFreefowBucket(a.FiveMinuteBucket)).ToList();
        var con = edge.Where(a => IsConstrainedBucket(a.FiveMinuteBucket)).ToList();
        var all = edge.Select(a => a.SumSpeedKmh / Math.Max(1, a.SampleCount)).Average();
        freeflow    = ff.Count  > 0 ? ff.Average(EdgeAvg)  : all;
        constrained = con.Count > 0 ? con.Average(EdgeAvg) : all;
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
                Latitude  = o.Latitude,
                Longitude = o.Longitude,
                SpeedMs   = o.SpeedMs,
                SpeedKmh  = Math.Round(o.SpeedMs * 3.6, 1),
                Heading   = o.Heading,
                OsmWayId  = o.OsmWayId,
                IsForward = o.IsForward,
                Timestamp = o.Timestamp
            })
            .ToListAsync(ct);

        return new LiveTrafficResponse
        {
            Points     = obs,
            TotalPoints = obs.Count,
            AsOf       = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Live edge-level traffic CSV (rolling 30-minute window, all matched edges).
    /// Format: osm_way_id,is_forward,avg_speed_kmh,sample_count
    /// </summary>
    public async Task WriteLiveEdgeCsvAsync(Stream output, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff && o.OsmWayId != null)
            .Select(o => new { o.OsmWayId, o.IsForward, o.SpeedMs })
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,is_forward,avg_speed_kmh,sample_count");

        foreach (var g in obs.GroupBy(o => (o.OsmWayId!.Value, o.IsForward ?? true))
                             .OrderBy(g => g.Key.Item1))
        {
            var avgKmh = g.Average(o => o.SpeedMs) * 3.6;
            await writer.WriteLineAsync(
                $"{g.Key.Item1},{(g.Key.Item2 ? 1 : 0)},{avgKmh:F1},{g.Count()}");
        }
    }

    /// <summary>
    /// Live H3-grid traffic CSV (rolling 30-minute window, resolution 9 cells).
    /// Format: h3_index,avg_speed_kmh,sample_count
    /// h3_index is the H3 cell id as an unsigned 64-bit hex string (e.g. 89283082803ffff)
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

        foreach (var g in obs.GroupBy(o => o.H3CellIndex!.Value)
                             .OrderBy(g => g.Key))
        {
            var avgKmh = g.Average(o => o.SpeedMs) * 3.6;
            // Output H3 index as canonical hex string
            var hexIndex = ((ulong)g.Key).ToString("x");
            await writer.WriteLineAsync($"{hexIndex},{avgKmh:F1},{g.Count()}");
        }
    }
}
