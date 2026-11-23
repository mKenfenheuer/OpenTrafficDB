using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.App.Services;
using OpenTrafficDBData;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>
/// Way geometries for the speed matcher, read from <c>WayGeometries</c> on demand and kept in memory
/// (<c>Pipeline:WayShapeCacheEntries</c>, default 1,000,000 ways).
///
/// The matcher needs them only for compacted router edges, which cover several OSM ways: a match
/// plan assigns each point to the member ways it actually lies on (see
/// <see cref="SpeedMatchingWorker"/>). Shapes are stored as interleaved latitude/longitude floats;
/// ways without geometry are cached as empty arrays so they are not queried again.
/// </summary>
public sealed class WayShapeCache(IServiceScopeFactory scopeFactory, IConfiguration config,
    ILogger<WayShapeCache> logger)
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = long.TryParse(config["Pipeline:WayShapeCacheEntries"], out var n) ? n : 1_000_000
    });
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>Shapes of the given ways (empty array if unknown). Never throws: on a database error the result lacks the missing ways.</summary>
    public async Task<Dictionary<long, float[]>> GetAsync(IReadOnlyCollection<long> wayIds, CancellationToken ct)
    {
        var result = new Dictionary<long, float[]>(wayIds.Count);
        var missing = new List<long>();
        foreach (var id in wayIds)
        {
            if (_cache.TryGetValue(id, out float[]? s)) result[id] = s!;
            else missing.Add(id);
        }
        if (missing.Count == 0) return result;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ReadOnlyAppDbContext>();
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT "OsmWayId", ST_AsEncodedPolyline("Geometry", 6)
                FROM "WayGeometries" WHERE "OsmWayId" = ANY(@ids) AND "Geometry" IS NOT NULL
                """, conn);
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = missing.ToArray() });
            var found = new Dictionary<long, float[]>(missing.Count);
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(1)) continue;
                    var pts = PolylineEncoder.Decode(reader.GetString(1)).ToList();
                    var shape = new float[pts.Count * 2];
                    for (int i = 0; i < pts.Count; i++) { shape[2 * i] = (float)pts[i].Lat; shape[2 * i + 1] = (float)pts[i].Lon; }
                    found[reader.GetInt64(0)] = shape;
                }
            }
            foreach (var id in missing)
            {
                var shape = found.GetValueOrDefault(id) ?? [];
                _cache.Set(id, shape, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = Ttl });
                result[id] = shape;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Way shapes for {Count} ways could not be read.", missing.Count);
        }
        return result;
    }
}
