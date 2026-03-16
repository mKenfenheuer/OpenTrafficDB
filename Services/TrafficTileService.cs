using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;

namespace OpenTrafficDB.Services;

/// <summary>
/// Builds gzip-compressed Mapbox Vector Tile (MVT) bytes for the "traffic" layer.
/// Each feature is a LineString representing an OSM way, attributed with current
/// speed, delay, jam severity, and source metadata.
/// </summary>
public class TrafficTileService(AppDbContext db)
{
    /// <summary>
    /// Build a traffic overlay tile for the given XYZ coordinate.
    /// Returns gzip-compressed MVT bytes (Content-Type: application/vnd.mapbox-vector-tile).
    /// </summary>
    public async Task<byte[]> BuildTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        var (minLat, minLon, maxLat, maxLon) = TileMath.TileBounds(z, x, y);

        // Query way geometries whose bounding box overlaps this tile
        var ways = await db.WayGeometries
            .Where(g => g.MinLat <= maxLat && g.MaxLat >= minLat
                     && g.MinLon <= maxLon && g.MaxLon >= minLon)
            .ToListAsync(ct);

        if (ways.Count == 0)
            return GzipCompress(MvtEncoder.Encode([new MvtLayer("traffic", [])]));

        // Load precomputed summaries for the matched ways (fast single query)
        var wayIds = ways.Select(w => w.OsmWayId).ToList();
        var summaries = await db.WaySpeedSummaries
            .Where(s => wayIds.Contains(s.OsmWayId))
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var features = new List<MvtFeature>(summaries.Count);

        foreach (var way in ways)
        {
            if (!summaries.TryGetValue(way.OsmWayId, out var s)) continue;

            // Project decoded polyline to tile pixel coordinates
            var pixels = PolylineEncoder.Decode(way.EncodedPolyline)
                .Select(p => TileMath.LatLonToPixel(p.Lat, p.Lon, z, x, y))
                .ToList();

            if (pixels.Count < 2) continue;

            // Current speed: live constrained if available, else freeflow
            var speedKmh = s.ConstrainedSpeed ?? s.FreeflowSpeed;
            var source   = s.ConstrainedSpeed.HasValue ? "live" : "historic";

            // Delay / jam relative to freeflow
            double? delayFactor = null, jamEstimate = null;
            var limit = s.SpeedLimitKmh ?? way.MaxSpeedKmh;
            if (limit is > 0)
            {
                var delay = Math.Clamp(1.0 - speedKmh / limit.Value, 0.0, 1.0);
                delayFactor = Math.Round(delay, 3);
                jamEstimate = Math.Round(TrafficAggregationService.ComputeJam(delay), 3);
            }

            var tags = new Dictionary<string, MvtValue>
            {
                ["osm_way_id"]    = new MvtValue.Int64Value(way.OsmWayId),
                ["speed_kmh"]     = new MvtValue.DoubleValue(Math.Round(speedKmh, 1)),
                ["freeflow_kmh"]  = new MvtValue.DoubleValue(s.FreeflowSpeed),
                ["p50_kmh"]       = new MvtValue.DoubleValue(s.P50Speed),
                ["p95_kmh"]       = new MvtValue.DoubleValue(s.P95Speed),
                ["source"]        = new MvtValue.StringValue(source),
                ["total_samples"] = new MvtValue.Int64Value(s.TotalSamples),
            };

            if (limit.HasValue)
                tags["speed_limit_kmh"] = new MvtValue.DoubleValue(limit.Value);
            if (delayFactor.HasValue)
                tags["delay_factor"]    = new MvtValue.DoubleValue(delayFactor.Value);
            if (jamEstimate.HasValue)
                tags["jam_estimate"]    = new MvtValue.DoubleValue(jamEstimate.Value);

            features.Add(new MvtFeature(
                Id:       (ulong)Math.Abs(way.OsmWayId),
                Geometry: pixels,
                Tags:     tags));
        }

        var tile = MvtEncoder.Encode([new MvtLayer("traffic", features)]);
        return GzipCompress(tile);
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(data);
        return ms.ToArray();
    }
}
