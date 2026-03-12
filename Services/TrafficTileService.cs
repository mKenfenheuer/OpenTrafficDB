using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;

namespace OpenTrafficDB.Services;

/// <summary>
/// Builds gzip-compressed Mapbox Vector Tile (MVT) bytes for the "traffic" layer.
/// Each feature is a LineString representing an OSM way, attributed with current
/// speed, delay, jam severity, and source metadata.
/// </summary>
public class TrafficTileService(AppDbContext db, TrafficAggregationService aggregation)
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

        // Get current traffic for the matched ways
        var traffic = await aggregation.GetCurrentWayTrafficAsync(
            ways.Select(w => w.OsmWayId), ct);

        var features = new List<MvtFeature>(traffic.Count);

        foreach (var way in ways)
        {
            if (!traffic.TryGetValue(way.OsmWayId, out var td)) continue;

            // Project decoded polyline to tile pixel coordinates
            var pixels = PolylineEncoder.Decode(way.EncodedPolyline)
                .Select(p => TileMath.LatLonToPixel(p.Lat, p.Lon, z, x, y))
                .ToList();

            if (pixels.Count < 2) continue;

            // Build feature attributes
            var tags = new Dictionary<string, MvtValue>
            {
                ["osm_way_id"]       = new MvtValue.Int64Value(way.OsmWayId),
                ["speed_kmh"]        = new MvtValue.DoubleValue(td.SpeedKmh),
                ["source"]           = new MvtValue.StringValue(td.Source),
                ["live_samples"]     = new MvtValue.Int64Value(td.LiveSamples),
                ["historic_samples"] = new MvtValue.Int64Value(td.HistoricSamples),
            };

            if (td.SpeedLimitKmh.HasValue)
                tags["speed_limit_kmh"] = new MvtValue.DoubleValue(td.SpeedLimitKmh.Value);
            if (td.DelayFactor.HasValue)
                tags["delay_factor"]    = new MvtValue.DoubleValue(td.DelayFactor.Value);
            if (td.JamEstimate.HasValue)
                tags["jam_estimate"]    = new MvtValue.DoubleValue(td.JamEstimate.Value);
            if (way.MaxSpeedKmh.HasValue)
                tags["osm_max_speed"]   = new MvtValue.DoubleValue(way.MaxSpeedKmh.Value);

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
