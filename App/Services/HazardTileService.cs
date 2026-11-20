using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using OpenTrafficDBData;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Builds gzip-compressed MVT tiles for the "hazards" layer.
/// Active (non-expired) hazard aggregates are rendered as point features.
/// </summary>
public class HazardTileService(ReadOnlyAppDbContext db, IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions TileOptions =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) };

    public async Task<byte[]> BuildTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        var key = $"htile:{z}:{x}:{y}";
        var cached = await cache.GetAsync(key, ct);
        if (cached != null) return cached;

        var tile = await BuildTileInternalAsync(z, x, y, ct);
        await cache.SetAsync(key, tile, TileOptions, ct);
        return tile;
    }

    private async Task<byte[]> BuildTileInternalAsync(int z, int x, int y, CancellationToken ct)
    {
        var (minLat, minLon, maxLat, maxLon) = TileMath.TileBounds(z, x, y);

        // Tiles only include active, map-matched aggregates — unmatched ones have no
        // OsmWayId/StreetName yet so they aren't useful to navigation clients.
        // Location is the snapped (matched) point when available.
        var result = await db.Database.SqlQuery<byte[]>($$"""
            SELECT ST_AsMVT(q, 'hazards', 4096, 'geom') AS "Value"
            FROM (
                SELECT
                    ST_AsMVTGeom(
                        ST_SetSRID(ST_MakePoint(
                            COALESCE(ha."MatchedLongitude", ha."Longitude"),
                            COALESCE(ha."MatchedLatitude",  ha."Latitude")), 4326),
                        ST_MakeEnvelope({{minLon}}, {{minLat}}, {{maxLon}}, {{maxLat}}, 4326),
                        4096, 64, true
                    )                                   AS geom,
                    ha."OsmWayId"                       AS osm_way_id,
                    ha."HazardType"                     AS hazard_type,
                    ha."Heading"                        AS heading,
                    ha."StreetName"                     AS street_name,
                    ha."IsPermanent"                    AS is_permanent,
                    (SELECT jsonb_object_agg('metadata:' || key, value)
                     FROM jsonb_each(ha."Metadata"))    AS metadata_props,
                    ha."ConfirmationCount"               AS confirmation_count,
                    EXTRACT(EPOCH FROM ha."LastConfirmedAt")::bigint AS last_confirmed_epoch,
                    EXTRACT(EPOCH FROM ha."ExpiresAt")::bigint      AS expires_epoch
                FROM "HazardAggregates" ha
                WHERE ha."ExpiresAt" > NOW()
                  AND ha."OsmWayId" IS NOT NULL
                  -- Road closures are stored as points every 300 m along the closed road; they are
                  -- drawn as hatched lines in the traffic tiles ("closures" layer), never as pins.
                  AND ha."HazardType" <> 'RoadClosure'
                  -- Raw position prefilter (IX_HazardAggregates_Latitude_Longitude): the matched
                  -- point is at most a few metres from it; the COALESCE filter alone is a full scan.
                  AND ha."Latitude"  BETWEEN {{minLat - 0.002}} AND {{maxLat + 0.002}}
                  AND ha."Longitude" BETWEEN {{minLon - 0.003}} AND {{maxLon + 0.003}}
                  AND COALESCE(ha."MatchedLatitude",  ha."Latitude")  BETWEEN {{minLat}} AND {{maxLat}}
                  AND COALESCE(ha."MatchedLongitude", ha."Longitude") BETWEEN {{minLon}} AND {{maxLon}}
            ) q
            WHERE q.geom IS NOT NULL
            """)
            .FirstOrDefaultAsync(ct);

        var mvtBytes = result;
        if (mvtBytes is not { Length: > 0 })
            mvtBytes = MvtEncoder.Encode([new MvtLayer("hazards", [])]);

        return GzipCompress(mvtBytes);
    }

    /// <summary>
    /// Streams the live hazards CSV. Only currently-valid hazards (ExpiresAt in the
    /// future) are included — expired entries are filtered out.
    /// </summary>
    public async Task WriteHazardsLiveCsv(Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 65536, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,hazard_type,lat,lon,heading,street_name,is_permanent,expires_at,metadata");

        var now = DateTime.UtcNow;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        await foreach (var s in db.HazardAggregates
            .Where(h => h.ExpiresAt > now && h.OsmWayId != null)
            .OrderBy(s => s.OsmWayId)
            .AsNoTracking()
            .Select(s => new
            {
                s.OsmWayId,
                s.HazardType,
                Lat         = s.MatchedLatitude  ?? s.Latitude,
                Lon         = s.MatchedLongitude ?? s.Longitude,
                s.Heading,
                s.StreetName,
                s.IsPermanent,
                s.ExpiresAt,
                s.Metadata
            })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            await writer.WriteLineAsync(
                $"{s.OsmWayId},{s.HazardType}," +
                $"{s.Lat.ToString(culture)},{s.Lon.ToString(culture)}," +
                $"{s.Heading.ToString(culture)}," +
                $"\"{s.StreetName?.Replace("\"","\"\"")}\"," +
                $"{s.IsPermanent}," +
                $"{s.ExpiresAt:O}," +
                $"\"{s.Metadata?.Replace("\"","\"\"")}\"");
        }
    }

    private static byte[] GzipCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(data);
        return ms.ToArray();
    }
}
