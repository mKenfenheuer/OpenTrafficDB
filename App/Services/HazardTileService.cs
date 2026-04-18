using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Services;

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

        var result = await db.Database.SqlQuery<byte[]>($$"""
            SELECT ST_AsMVT(q, 'hazards', 4096, 'geom') AS "Value"
            FROM (
                SELECT
                    ST_AsMVTGeom(
                        ST_SetSRID(ST_MakePoint(ha."Longitude", ha."Latitude"), 4326),
                        ST_MakeEnvelope({{minLon}}, {{minLat}}, {{maxLon}}, {{maxLat}}, 4326),
                        4096, 64, true
                    )                                   AS geom,
                    ha."OsmWayId"                       AS osm_way_id,
                    ha."HazardType"                     AS hazard_type,
                    (SELECT jsonb_object_agg('metadata:' || key, value)
                     FROM jsonb_each(ha."Metadata"))    AS metadata_props,
                    ha."ConfirmationCount"               AS confirmation_count,
                    EXTRACT(EPOCH FROM ha."LastConfirmedAt")::bigint AS last_confirmed_epoch,
                    EXTRACT(EPOCH FROM ha."ExpiresAt")::bigint      AS expires_epoch
                FROM "HazardAggregates" ha
                WHERE ha."ExpiresAt" > NOW()
                  AND ha."Latitude"  BETWEEN {{minLat}} AND {{maxLat}}
                  AND ha."Longitude" BETWEEN {{minLon}} AND {{maxLon}}
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
    /// Streams the live hazards CSV.
    /// </summary>
    public async Task WriteHazardsLiveCsv(Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 65536, leaveOpen: true);
        await writer.WriteLineAsync("osm_way_id,hazard_type,lat,lon,metadata");

        await foreach (var s in db.HazardAggregates
            .OrderBy(s => s.OsmWayId)
            .AsNoTracking()
            .Select(s => new { s.OsmWayId, s.HazardType, s.Latitude, s.Longitude, s.Metadata })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            await writer.WriteLineAsync($"{s.OsmWayId},{s.HazardType},{s.Latitude},{s.Longitude},\"{s.Metadata?.Replace("\"","\"\"")}\"");
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
