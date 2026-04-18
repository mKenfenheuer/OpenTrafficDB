using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using OpenTrafficDBData;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Builds gzip-compressed Mapbox Vector Tile (MVT) bytes for the "traffic" layer.
/// Geometry clipping, simplification, and MVT encoding are performed by PostGIS
/// (ST_AsMVTGeom + ST_AsMVT) to avoid per-coordinate mercator projection in C#.
/// Tiles are cached in Redis (IDistributedCache) for 60 seconds.
/// </summary>
public class TrafficTileService(ReadOnlyAppDbContext db, IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions TileOptions =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) };

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

        // PostGIS does all the heavy lifting:
        //   - GIST bbox filter on WayGeometries.BBox
        //   - ST_AsMVTGeom: clips to tile + 64px buffer, re-projects to tile coordinates,
        //     simplifies geometry (Douglas-Peucker) — returns NULL for geometries fully outside
        //   - ST_AsMVT: encodes features + tags into MVT protobuf bytes
        //
        // Derived fields (delay, jam) are computed in a nested subquery so the delay value
        // is not repeated multiple times across the CASE expressions.
        var result = await db.Database.SqlQuery<byte[]>($"""
            SELECT ST_AsMVT(q, 'traffic', 4096, 'geom') AS "Value"
            FROM (
                SELECT
                    ST_AsMVTGeom(
                        base."Geometry",
                        ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326),
                        4096, 64, true
                    )                                                                   AS geom,
                    base."OsmWayId"                                                     AS osm_way_id,
                    ROUND(base.speed_kmh::numeric, 1)::float8                          AS speed_kmh,
                    base.freeflow_kmh,
                    CASE WHEN base."ConstrainedSpeed" IS NOT NULL THEN 'live'
                         ELSE 'historic' END                                            AS source,
                    base.speed_limit_kmh,
                    CASE WHEN base."FreeflowSpeed" IS NOT NULL
                         THEN ROUND(base.delay::numeric, 3)::float8 END                AS delay_factor,
                    CASE WHEN base."FreeflowSpeed" IS NOT NULL
                         THEN ROUND((CASE
                                 WHEN base.delay <= 0    THEN 0.0
                                 WHEN base.delay <= 0.6  THEN base.delay * (0.7 / 0.6)
                                 WHEN base.delay <= 0.85 THEN 0.7 + (base.delay - 0.6) * (0.3 / 0.25)
                                 ELSE 1.0
                             END)::numeric, 3)::float8 END                             AS jam_estimate
                FROM (
                    SELECT
                        wg."OsmWayId",
                        wg."Geometry",
                        wss."FreeflowSpeed",
                        wss."ConstrainedSpeed",
                        COALESCE(wss."ConstrainedSpeed", wss."FreeflowSpeed")           AS speed_kmh,
                        COALESCE(wss."SpeedLimitKmh", wg."MaxSpeedKmh")                AS speed_limit_kmh,
                        LEAST(wss."FreeflowSpeed",
                              COALESCE(wss."SpeedLimitKmh", wg."MaxSpeedKmh",
                                       wss."FreeflowSpeed"))                            AS freeflow_kmh,
                        LEAST(GREATEST(
                            1.0 - COALESCE(wss."ConstrainedSpeed", wss."FreeflowSpeed")
                                / NULLIF(LEAST(wss."FreeflowSpeed",
                                               COALESCE(wss."SpeedLimitKmh", wg."MaxSpeedKmh",
                                                        wss."FreeflowSpeed")), 0),
                            0.0), 1.0)                                                  AS delay
                    FROM "WayGeometries" wg
                    JOIN "WaySpeedSummaries" wss ON wss."OsmWayId" = wg."OsmWayId"
                                                AND wss."LastUpdated" >= NOW() - INTERVAL '60 minutes'
                    WHERE wg."BBox" && ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326)
                ) base
            ) q
            WHERE q.geom IS NOT NULL
            """)
            .FirstOrDefaultAsync(ct);

        // ST_AsMVT returns empty bytea (zero bytes) when no features match — treat as empty tile.
        var mvtBytes = result;
        if (mvtBytes is not { Length: > 0 })
            mvtBytes = MvtEncoder.Encode([new MvtLayer("traffic", [])]);

        return GzipCompress(mvtBytes);
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(data);
        return ms.ToArray();
    }
}
