using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using OpenTrafficDB.Shared.Data;

namespace OpenTrafficDB.Shared.Services;

/// <summary>
/// Builds gzip-compressed Mapbox Vector Tile (MVT) bytes for the "traffic" layer.
/// Tiles are cached in Redis (IDistributedCache) for 30 seconds.
/// </summary>
public class TrafficTileService(ReadOnlyAppDbContext db, IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions TileOptions =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) };

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

        // Use raw SQL with ST_MakeEnvelope so the bbox is expressed as scalar doubles —
        // avoids passing a Geometry object as a parameter, which requires NTS type mapping
        // at the Npgsql data source level and fails with PgBouncer pooled connections.
        // The GIST index on BBox is still used by Postgres via the && operator.
        var ways = await db.WayGeometries
            .FromSqlInterpolated($"""
                SELECT * FROM "WayGeometries"
                WHERE "BBox" && ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326)
                """)
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

            if (way.Geometry == null) continue;
            var pixels = way.Geometry.Coordinates
                .Select(c => TileMath.LatLonToPixel(c.Y, c.X, z, x, y))
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
