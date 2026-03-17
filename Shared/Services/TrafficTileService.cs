using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using NetTopologySuite.Geometries;
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
        var rows = await db.Database.SqlQuery<WayWithSummary>($"""
                SELECT
                    wg."OsmWayId",
                    wg."Geometry",
                    wg."MaxSpeedKmh",
                    wss."FreeflowSpeed",
                    wss."ConstrainedSpeed",
                    wss."P50Speed",
                    wss."P95Speed",
                    wss."SpeedLimitKmh",
                    wss."TotalSamples"
                FROM "WayGeometries" wg
                JOIN "WaySpeedSummaries" wss ON wss."OsmWayId" = wg."OsmWayId"
                WHERE wg."BBox" && ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326)
                """)
            .AsNoTracking()
            .ToListAsync(ct);

        if (rows.Count == 0)
            return GzipCompress(MvtEncoder.Encode([new MvtLayer("traffic", [])]));

        var features = new List<MvtFeature>(rows.Count);

        foreach (var row in rows)
        {
            if (row.Geometry == null) continue;
            var pixels = row.Geometry.Coordinates
                .Select(c => TileMath.LatLonToPixel(c.Y, c.X, z, x, y))
                .ToList();

            if (pixels.Count < 2) continue;

            var speedKmh = row.ConstrainedSpeed ?? row.FreeflowSpeed;
            var source   = row.ConstrainedSpeed.HasValue ? "live" : "historic";

            double? delayFactor = null, jamEstimate = null;
            var limit = row.SpeedLimitKmh ?? row.MaxSpeedKmh;
            if (limit is > 0)
            {
                var delay = Math.Clamp(1.0 - speedKmh / limit.Value, 0.0, 1.0);
                delayFactor = Math.Round(delay, 3);
                jamEstimate = Math.Round(TrafficAggregationService.ComputeJam(delay), 3);
            }

            var tags = new Dictionary<string, MvtValue>
            {
                ["osm_way_id"]    = new MvtValue.Int64Value(row.OsmWayId),
                ["speed_kmh"]     = new MvtValue.DoubleValue(Math.Round(speedKmh, 1)),
                ["freeflow_kmh"]  = new MvtValue.DoubleValue(row.FreeflowSpeed),
                ["p50_kmh"]       = new MvtValue.DoubleValue(row.P50Speed),
                ["p95_kmh"]       = new MvtValue.DoubleValue(row.P95Speed),
                ["source"]        = new MvtValue.StringValue(source),
                ["total_samples"] = new MvtValue.Int64Value(row.TotalSamples),
            };

            if (limit.HasValue)          tags["speed_limit_kmh"] = new MvtValue.DoubleValue(limit.Value);
            if (delayFactor.HasValue)    tags["delay_factor"]    = new MvtValue.DoubleValue(delayFactor.Value);
            if (jamEstimate.HasValue)    tags["jam_estimate"]    = new MvtValue.DoubleValue(jamEstimate.Value);

            features.Add(new MvtFeature(
                Id:       (ulong)Math.Abs(row.OsmWayId),
                Geometry: pixels,
                Tags:     tags));
        }

        return GzipCompress(MvtEncoder.Encode([new MvtLayer("traffic", features)]));
    }

    // Flat projection used by the single-query JOIN — avoids loading full entity graphs.
    private sealed class WayWithSummary
    {
        public long     OsmWayId         { get; set; }
        public Geometry? Geometry        { get; set; }
        public double?  MaxSpeedKmh      { get; set; }
        // WaySpeedSummary columns
        public double   FreeflowSpeed    { get; set; }
        public double?  ConstrainedSpeed { get; set; }
        public double   P50Speed         { get; set; }
        public double   P95Speed         { get; set; }
        public double?  SpeedLimitKmh    { get; set; }
        public int      TotalSamples     { get; set; }
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(data);
        return ms.ToArray();
    }
}
