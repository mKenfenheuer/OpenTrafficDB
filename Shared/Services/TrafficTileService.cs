using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using NetTopologySuite.Geometries;
using OpenTrafficDB.App.Data;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Builds gzip-compressed Mapbox Vector Tile (MVT) bytes for the "traffic" layer.
/// Tiles are cached in Redis (IDistributedCache) for 30 seconds.
/// </summary>
public class TrafficTileService(ReadOnlyAppDbContext db, IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions TileOptions =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) };

    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    public async Task<byte[]> BuildTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        var key = $"tile:{z}:{x}:{y}";
        var cached = await cache.GetAsync(key, ct);
        if (cached != null) return cached;

        var tile = await BuildTileInternalAsync(z, x, y, ct);
        await cache.SetAsync(key, tile, TileOptions, ct);
        return tile;
    }

    private async Task<byte[]> BuildTileInternalAsync(int z, int x, int y, CancellationToken ct)
    {
        var (minLat, minLon, maxLat, maxLon) = TileMath.TileBounds(z, x, y);

        // Build a bbox polygon and use the PostGIS GIST index via ST_Intersects
        var tileBbox = Factory.CreatePolygon([
            new Coordinate(minLon, minLat),
            new Coordinate(maxLon, minLat),
            new Coordinate(maxLon, maxLat),
            new Coordinate(minLon, maxLat),
            new Coordinate(minLon, minLat)
        ]);
        tileBbox.SRID = 4326;

        var ways = await db.WayGeometries
            .Where(g => g.BBox != null && g.BBox.Intersects(tileBbox))
            .AsNoTracking()
            .ToListAsync(ct);

        if (ways.Count == 0)
            return GzipCompress(MvtEncoder.Encode([new MvtLayer("traffic", [])]));

        var wayIds = ways.Select(w => w.OsmWayId).ToList();
        var summaries = await db.WaySpeedSummaries
            .Where(s => wayIds.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var features = new List<MvtFeature>(summaries.Count);

        foreach (var way in ways)
        {
            if (!summaries.TryGetValue(way.OsmWayId, out var s)) continue;

            var pixels = PolylineEncoder.Decode(way.EncodedPolyline)
                .Select(p => TileMath.LatLonToPixel(p.Lat, p.Lon, z, x, y))
                .ToList();

            if (pixels.Count < 2) continue;

            var speedKmh = s.ConstrainedSpeed ?? s.FreeflowSpeed;
            var source   = s.ConstrainedSpeed.HasValue ? "live" : "historic";

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

            if (limit.HasValue)          tags["speed_limit_kmh"] = new MvtValue.DoubleValue(limit.Value);
            if (delayFactor.HasValue)    tags["delay_factor"]    = new MvtValue.DoubleValue(delayFactor.Value);
            if (jamEstimate.HasValue)    tags["jam_estimate"]    = new MvtValue.DoubleValue(jamEstimate.Value);

            features.Add(new MvtFeature(
                Id:       (ulong)Math.Abs(way.OsmWayId),
                Geometry: pixels,
                Tags:     tags));
        }

        return GzipCompress(MvtEncoder.Encode([new MvtLayer("traffic", features)]));
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(data);
        return ms.ToArray();
    }
}
