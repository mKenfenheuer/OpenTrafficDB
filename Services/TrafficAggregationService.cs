using System.Formats.Tar;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

public class TrafficAggregationService(AppDbContext db)
{
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

    // 5-minute bucket boundaries within a day (FiveMinuteBucket = minuteOfDay / 5, range 0-287)
    private static bool IsFreefowBucket(int bucket) => bucket < 60 || bucket >= 264; // midnight-5AM, 10PM-midnight
    private static bool IsConstrainedBucket(int bucket) => bucket is >= 84 and <= 228; // 7AM-7PM


    /// <summary>
    /// Recompute aggregates for all matched observations that have a ValhallaEdgeId.
    /// Called by the background service periodically.
    /// </summary>
    public async Task ReaggregateAsync(CancellationToken ct = default)
    {
        // Fetch all observations with a matched edge ID, streamed to avoid huge memory usage
        var matched = await db.SpeedObservations
            .Where(o => o.ValhallaEdgeId != null)
            .Select(o => new
            {
                o.ValhallaEdgeId,
                o.Timestamp,
                o.SpeedMs
            })
            .ToListAsync(ct);

        if (matched.Count == 0) return;

        // Compute new aggregates in memory
        var buckets = new Dictionary<(long EdgeId, int DayOfWeek, int Bucket), (double SumSpeed, int Count)>();

        foreach (var obs in matched)
        {
            var ts = obs.Timestamp.ToUniversalTime();
            var dow = (int)ts.DayOfWeek;
            var bucket = (ts.Hour * 60 + ts.Minute) / 5;
            var key = (obs.ValhallaEdgeId!.Value, dow, bucket);

            if (buckets.TryGetValue(key, out var agg))
                buckets[key] = (agg.SumSpeed + obs.SpeedMs * 3.6, agg.Count + 1);
            else
                buckets[key] = (obs.SpeedMs * 3.6, 1);
        }

        // Upsert into RoadSpeedAggregates
        var now = DateTime.UtcNow;
        foreach (var kvp in buckets)
        {
            var (edgeId, dow, bucket) = kvp.Key;
            var (sumSpeed, count) = kvp.Value;
            var avgKmh = sumSpeed / count;

            var existing = await db.RoadSpeedAggregates
                .FirstOrDefaultAsync(a => a.ValhallaEdgeId == edgeId
                    && a.DayOfWeek == dow
                    && a.FiveMinuteBucket == bucket, ct);

            if (existing == null)
            {
                db.RoadSpeedAggregates.Add(new RoadSpeedAggregate
                {
                    ValhallaEdgeId = edgeId,
                    DayOfWeek = dow,
                    FiveMinuteBucket = bucket,
                    AvgSpeedKmh = avgKmh,
                    SampleCount = count,
                    LastUpdated = now
                });
            }
            else
            {
                existing.AvgSpeedKmh = avgKmh;
                existing.SampleCount = count;
                existing.LastUpdated = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Export all predicted traffic edges as a single Valhalla-compatible CSV.
    /// Format: edge_id,freeflow_speed,constrained_speed
    ///   edge_id          = "level/tile_id/id" (Valhalla GraphId string form)
    ///   freeflow_speed   = average km/h during night hours (midnight–5 AM, 10 PM–midnight)
    ///   constrained_speed = average km/h during day hours (7 AM–7 PM)
    /// Feed to: valhalla_add_predicted_traffic -t &lt;traffic_folder&gt;
    /// </summary>
    public async Task WritePredictedCsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.RoadSpeedAggregates
            .OrderBy(a => a.ValhallaEdgeId)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);

        foreach (var edge in aggregates.GroupBy(a => a.ValhallaEdgeId))
        {
            ComputeSpeeds(edge, out var freeflow, out var constrained);
            if (freeflow <= 0 && constrained <= 0) continue;

            await writer.WriteLineAsync(
                $"{GraphIdToEdgeString(edge.Key)},{(int)Math.Round(freeflow)},{(int)Math.Round(constrained)}".AsMemory(), ct);
        }
    }

    /// <summary>
    /// Build a tar archive containing one CSV per Valhalla tile, organised in the tile folder
    /// hierarchy expected by valhalla_add_predicted_traffic (-t &lt;folder&gt;).
    ///
    /// Hierarchy: {level}/{tile_dir:D3}/{tile_file:D3}.csv
    ///   e.g. tile_id 47701 at level 1 → "1/047/701.csv"
    ///
    /// Each CSV row: edge_id,freeflow_speed,constrained_speed
    /// </summary>
    public async Task WriteTrafficTarAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.RoadSpeedAggregates
            .OrderBy(a => a.ValhallaEdgeId)
            .ToListAsync(ct);

        // Group edges by tile so we can write one CSV file per tile entry in the tar.
        var byTile = aggregates
            .GroupBy(a => GraphIdToTilePath(a.ValhallaEdgeId))
            .OrderBy(g => g.Key);

        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);

        foreach (var tile in byTile)
        {
            // Build CSV content for this tile
            var csv = new StringBuilder();
            foreach (var edge in tile.GroupBy(a => a.ValhallaEdgeId))
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

    private static void ComputeSpeeds(IGrouping<long, RoadSpeedAggregate> edge,
        out double freeflow, out double constrained)
    {
        var freeflowSamples = edge.Where(a => IsFreefowBucket(a.FiveMinuteBucket)).ToList();
        var constrainedSamples = edge.Where(a => IsConstrainedBucket(a.FiveMinuteBucket)).ToList();
        var fallback = edge.Average(a => a.AvgSpeedKmh);

        freeflow    = freeflowSamples.Count    > 0 ? freeflowSamples.Average(a => a.AvgSpeedKmh)    : fallback;
        constrained = constrainedSamples.Count > 0 ? constrainedSamples.Average(a => a.AvgSpeedKmh) : fallback;
    }

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
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                SpeedMs = o.SpeedMs,
                SpeedKmh = Math.Round(o.SpeedMs * 3.6, 1),
                Heading = o.Heading,
                ValhallaEdgeId = o.ValhallaEdgeId,
                Timestamp = o.Timestamp
            })
            .ToListAsync(ct);

        return new LiveTrafficResponse
        {
            Points = obs,
            TotalPoints = obs.Count,
            AsOf = DateTime.UtcNow
        };
    }
}
