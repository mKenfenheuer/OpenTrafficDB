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
            await cache.SetAsync(key, tile, z <= 10 ? LowZoomOptions : TileOptions, CancellationToken.None);
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
        var sql = $$"""
            SELECT (SELECT ST_AsMVT(q, 'traffic', 4096, 'geom')
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
                                                AND wss."LastUpdated" >= {4}
                    WHERE wg."BBox" && ST_MakeEnvelope({0}, {1}, {2}, {3}, 4326)
                      {{classFilter}}
                      AND COALESCE(wss."RoadClass", wg."RoadClass") <= {6}
                ) base
                WHERE COALESCE(base.delay, 0) >= {5}
            ) q
            WHERE q.geom IS NOT NULL)
            -- Second layer: roads with an active closure, independent of live speed data. Driven by
            -- the (few) active closures near the tile instead of probing every road in the tile.
            || (SELECT ST_AsMVT(c, 'closures', 4096, 'geom')
                FROM (
                    SELECT ST_AsMVTGeom(wg."Geometry", ST_MakeEnvelope({0}, {1}, {2}, {3}, 4326), 4096, 64, true) AS geom,
                           wg."OsmWayId" AS osm_way_id
                    FROM "WayGeometries" wg
                    WHERE wg."OsmWayId" IN (
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
        var result = await db.Database
            .SqlQueryRaw<byte[]>(sql, minLon, minLat, maxLon, maxLat, liveCutoff, _minDelay, (short)maxRoadClass)
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
