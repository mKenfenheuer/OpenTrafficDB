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
/// Tiles are cached in Redis (IDistributedCache) for Traffic:TileCacheSeconds (default 15 s).
/// </summary>
public class TrafficTileService(ReadOnlyAppDbContext db, IDistributedCache cache, IConfiguration config)
{
    // Must be longer than the refresh cycle of the data sources (TomTom importer: ~24 min at zoom 10).
    private readonly TimeSpan _liveWindow = TimeSpan.FromMinutes(
        double.TryParse(config["Traffic:LiveWindowMinutes"], out var lw) ? lw : 60);

    // Minimum delay factor (0–1) a road needs to appear in the tiles. 0 = all roads with data.
    private readonly double _minDelay = double.TryParse(config["Traffic:TileMinDelay"],
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var md) ? md : 0.0;

    // Short cache so the map stays near-live: the speed pipeline updates live speeds every ~10 s.
    private readonly DistributedCacheEntryOptions TileOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(
            double.TryParse(config["Traffic:TileCacheSeconds"], out var tc) ? tc : 15)
    };

    // Low-zoom tiles cover whole regions: expensive to build, and one road changing barely shows.
    private static readonly DistributedCacheEntryOptions LowZoomOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    /// <summary>
    /// Highest zoom whose "traffic" layer holds merged per-delay-band features (only delay_factor)
    /// instead of one feature per OSM way. Cuts a z6 tile from ~2.1 MB to ~140 KB (uncompressed).
    /// </summary>
    public const int MergedMaxZoom = 10;

    // MVT extent for merged tiles. With the 1-unit simplification the shape error stays below
    // ~0.4 px of a 512 px tile, far under the line width; 4096 would only add coordinate bytes.
    private const int MergedExtent = 2048;

    // Highest zoom built in Web Mercator (EPSG:3857) instead of linear lon/lat; see BuildTileInternalAsync.
    private const int MercatorMaxZoom = 8;

    // Requests for the same tile that arrive while it is being built share that one build.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]>> _inFlight = new();

    public async Task<byte[]> BuildTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        var key = $"tile:{z}:{x}:{y}";
        var cached = await cache.GetAsync(key, ct);
        if (cached != null) return cached;

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _inFlight.GetOrAdd(key, tcs.Task);
        if (running != tcs.Task) return await running.WaitAsync(ct);

        try
        {
            // Not cancelled by this request: other requests may be waiting for the result.
            var tile = await BuildTileInternalAsync(z, x, y, CancellationToken.None);
            await cache.SetAsync(key, tile, z <= MergedMaxZoom ? LowZoomOptions : TileOptions, CancellationToken.None);
            tcs.SetResult(tile);
            return tile;
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task<byte[]>>(key, tcs.Task));
        }
    }

    /// <summary>
    /// Highest road class shown at a zoom level, like a road map: motorways and trunk roads at
    /// country scale, smaller roads as you zoom in. Keeps low-zoom tiles small (they cover
    /// hundreds of thousands of roads otherwise). Roads without a class count as class 5.
    /// </summary>
    private static short MaxRoadClass(int z) => z switch
    {
        <= 8 => 1,   // motorway, trunk
        9 => 2,      // + primary
        10 => 3,     // + secondary
        11 => 4,     // + tertiary
        _ => 5,      // all roads
    };

    private async Task<byte[]> BuildTileInternalAsync(int z, int x, int y, CancellationToken ct)
    {
        var maxRoadClass = MaxRoadClass(z);
        var (minLat, minLon, maxLat, maxLon) = TileMath.TileBounds(z, x, y);

        // PostGIS does all the heavy lifting:
        //   - GIST bbox filter on WayGeometries.BBox
        //   - ST_AsMVTGeom: clips to tile + 64px buffer, re-projects to tile coordinates,
        //     simplifies geometry (Douglas-Peucker) — returns NULL for geometries fully outside
        //   - ST_AsMVT: encodes features + tags into MVT protobuf bytes
        //
        // Derived fields (delay, jam) are computed in a nested subquery so the delay value
        // is not repeated multiple times across the CASE expressions.
        var liveCutoff = DateTime.UtcNow - _liveWindow;

        // The road class filter on WayGeometries is written as a literal so the planner can use the
        // partial spatial index for that level (IX_WayGeometries_BBox_ClassN): a z8 tile then scans
        // ~8k motorway geometries instead of ~200k roads of all classes. Values are parameters.
        var classFilter = maxRoadClass < 5 ? $"AND wg.\"RoadClass\" <= {maxRoadClass}" : "";

        // Geometries are stored in EPSG:4326. Mapping lon/lat linearly onto the tile ignores the
        // Mercator stretch in y; that error grows with tile height (z6: ~5 px, z8: ~1.2 px at 512 px,
        // z9: 0.6 px). Up to MercatorMaxZoom, project to EPSG:3857 and clip to the exact tile
        // envelope. The BBox prefilter stays on the 4326 index (same tile bounds).
        var mercator = z <= MercatorMaxZoom;
        var trafficGeom = mercator ? "ST_Transform(base.\"Geometry\", 3857)" : "base.\"Geometry\"";
        var closureGeom = mercator ? "ST_Transform(wg.\"Geometry\", 3857)" : "wg.\"Geometry\"";
        var tileEnvelope = mercator
            ? "ST_TileEnvelope({7}, {8}, {9})"
            : "ST_MakeEnvelope({0}, {1}, {2}, {3}, 4326)";
        var baseSql = $$"""
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
                                                AND wss."LastUpdated" >= {4}
                    WHERE wg."BBox" && ST_MakeEnvelope({0}, {1}, {2}, {3}, 4326)
                      {{classFilter}}
                      AND COALESCE(wss."RoadClass", wg."RoadClass") <= {6}
            """;

        var trafficSql = z <= MergedMaxZoom
            // Overview zooms: one feature per delay band instead of one per OSM way. Roads in a band
            // are clipped per way, collected and line-merged into one MultiLineString, then simplified
            // by one tile unit (Douglas-Peucker; ST_AsMVTGeom only drops collinear points). Only the
            // band value is kept as delay_factor; NULL band = no free-flow speed known (grey).
            // Higher bands come last so congestion is drawn on top. ST_AsMVT skips NULL geometries.
            ? $$"""
            SELECT ST_AsMVT(q, 'traffic', {{MergedExtent}}, 'geom')
            FROM (
                SELECT
                    f.delay_factor,
                    ST_Simplify(ST_LineMerge(ST_CollectionExtract(ST_Collect(f.geom), 2)), 1) AS geom
                FROM (
                    SELECT
                        ST_AsMVTGeom({{trafficGeom}}, {{tileEnvelope}},
                                     {{MergedExtent}}, {{MergedExtent / 64}}, true)      AS geom,
                        CASE WHEN base."FreeflowSpeed" IS NOT NULL
                             THEN LEAST(ROUND(base.delay * 4) / 4, 0.75) END            AS delay_factor
                    FROM ({{baseSql}}) base
                    WHERE COALESCE(base.delay, 0) >= {5}
                ) f
                WHERE f.geom IS NOT NULL
                GROUP BY f.delay_factor
                ORDER BY f.delay_factor NULLS FIRST
            ) q
            """
            : $$"""
            SELECT ST_AsMVT(q, 'traffic', 4096, 'geom')
            FROM (
                SELECT
                    ST_AsMVTGeom(base."Geometry", ST_MakeEnvelope({0}, {1}, {2}, {3}, 4326), 4096, 64, true) AS geom,
                    base."OsmWayId"                                                     AS osm_way_id,
                    ROUND(base.speed_kmh::numeric, 1)::float8                          AS speed_kmh,
                    base.freeflow_kmh,
                    CASE WHEN base."ConstrainedSpeed" IS NOT NULL THEN 'live'
                         ELSE 'historic' END                                            AS source,
                    base.speed_limit_kmh,
                    CASE WHEN base."FreeflowSpeed" IS NOT NULL
                         THEN ROUND(base.delay::numeric, 3)::float8 END                AS delay_factor
                FROM ({{baseSql}}) base
                WHERE COALESCE(base.delay, 0) >= {5}
            ) q
            WHERE q.geom IS NOT NULL
            """;

        var sql = $$"""
            SELECT ({{trafficSql}})
            -- Second layer: roads with an active closure, independent of live speed data. Driven by
            -- the (few) active closures near the tile instead of probing every road in the tile.
            || (SELECT ST_AsMVT(c, 'closures', 4096, 'geom')
                FROM (
                    SELECT ST_AsMVTGeom({{closureGeom}}, {{tileEnvelope}}, 4096, 64, true) AS geom,
                           wg."OsmWayId" AS osm_way_id
                    FROM "WayGeometries" wg
                    WHERE wg."OsmWayId" IN (
                            SELECT c."OsmWayId" FROM "WayClosures" c WHERE c."ExpiresAt" > NOW()
                            UNION
                            SELECT h."OsmWayId" FROM "HazardAggregates" h
                            WHERE h."HazardType" = 'RoadClosure'
                              AND h."ExpiresAt" > NOW()
                              AND h."OsmWayId" IS NOT NULL
                              AND h."Latitude"  BETWEEN {1} - 0.05 AND {3} + 0.05
                              AND h."Longitude" BETWEEN {0} - 0.08 AND {2} + 0.08)
                      AND wg."BBox" && ST_MakeEnvelope({0}, {1}, {2}, {3}, 4326)
                      {{classFilter}}
                ) c
                WHERE c.geom IS NOT NULL) AS "Value"
            """;
        // JIT is off server-wide (docker-compose: jit=off): ST_Transform raises the z6 plan cost
        // above the JIT thresholds and compiling took ~1 s for a query that runs in ~0.4 s.
        var result = await db.Database
            .SqlQueryRaw<byte[]>(sql, minLon, minLat, maxLon, maxLat, liveCutoff, _minDelay, (short)maxRoadClass, z, x, y)
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
