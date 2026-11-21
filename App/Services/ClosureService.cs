using Npgsql;
using NpgsqlTypes;
using OpenTrafficDBData;
using OpenTrafficDBModels.DTOs;
using Microsoft.EntityFrameworkCore;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Turns closure polylines into closed roads (<c>WayClosures</c>).
///
/// Each line is map-matched as one trace (OpenTrafficRouting HMM): the matcher follows the road
/// network, so a closed connector or slip road stays on that road instead of jumping to a parallel
/// carriageway a few metres away, which happened when closure points were snapped one by one.
/// All roads of the matched path are closed, including those between the line's vertices.
/// </summary>
public class ClosureService(AppDbContext db, MapMatchingService matcher, ILogger<ClosureService> logger)
{
    private const int MaxPolylinesPerRequest = 1000;

    public async Task<ClosureBatchResponse> SubmitAsync(ClosureBatchRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lines = request.Closures
            .Where(l => l.Points.Count >= 2 && l.Points.All(p => p.Length >= 2
                && p[0] is >= -90 and <= 90 && p[1] is >= -180 and <= 180))
            .ToList();

        // wayId → (expires, source); the longest TTL wins when lines overlap.
        var closed = new Dictionary<long, (DateTime Expires, string Source)>();
        // Point-matched carriageway ways and the line's driving direction there, checked below.
        var directional = new List<(long WayId, double LineBearing, DateTime Expires, string Source)>();
        int matchedLines = 0;

        foreach (var group in lines.GroupBy(l => RoadClassFilter.Parse(l.RoadClass)))
        {
            var roadTypes = RoadClassFilter.ForHint(group.Key);
            var batch = group.ToList();
            for (int i = 0; i < batch.Count; i += MaxPolylinesPerRequest)
            {
                var chunk = batch.Skip(i).Take(MaxPolylinesPerRequest).ToList();
                var polylines = chunk.Select(l => MapMatchingService.EncodePolyline6(
                    l.Points.Select((p, k) => (p[0], p[1], now.AddSeconds(k))))).ToList();
                var results = await matcher.MatchTracesBulkAsync(polylines, roadTypes, ct);

                for (int j = 0; j < chunk.Count && j < results.Count; j++)
                {
                    var edges = results[j]?.Edges;
                    if (edges == null) continue;
                    var expires = now.AddMinutes(Math.Clamp(chunk[j].TtlMinutes, 5, 1440));
                    var source = chunk[j].Source ?? "unknown";
                    bool any = false;
                    var pts = chunk[j].Points;
                    foreach (var e in edges)
                    {
                        if (e.Edge == null || e.Edge.OsmWayId == 0
                            || MapMatchingService.IsExcludedRoadType(e.Edge.RoadType)) continue;
                        // Carriageways (one-way motorway/trunk roads) matched by a point: verify the
                        // direction below; at an interchange the end of a line can snap onto the
                        // opposite carriageway. Interpolated edges follow the routed path.
                        bool checkDirection = !e.IsInterpolated && IsCarriageway(e.Edge.RoadType);
                        double lineBearing = checkDirection ? LineBearing(pts, e.BeginPointIndex, e.EndPointIndex) : 0;
                        foreach (var wayId in MapMatchingService.WayIdsOf(e.Edge))
                        {
                            if (wayId == 0) continue;
                            any = true;
                            if (checkDirection) { directional.Add((wayId, lineBearing, expires, source)); continue; }
                            if (!closed.TryGetValue(wayId, out var cur) || cur.Expires < expires)
                                closed[wayId] = (expires, source);
                        }
                    }
                    if (any) matchedLines++;
                }
            }
        }

        int rejected = 0;
        if (directional.Count > 0)
        {
            var bearings = await WayBearingsAsync(directional.Select(d => d.WayId).Distinct().ToArray(), ct);
            foreach (var (wayId, lineBearing, expires, source) in directional)
            {
                // Unknown geometry or very short ways (bearing meaningless): keep.
                if (bearings.TryGetValue(wayId, out var wb) && wb.LengthM >= 30
                    && AngleDiff(wb.Bearing, lineBearing) > 100)
                {
                    rejected++;
                    continue;
                }
                if (!closed.TryGetValue(wayId, out var cur) || cur.Expires < expires)
                    closed[wayId] = (expires, source);
            }
        }

        if (closed.Count > 0)
            await UpsertAsync(closed, now, ct);

        logger.LogInformation("Closures: {Lines} lines, {Matched} matched, {Ways} roads closed, {Rejected} opposite-direction matches dropped.",
            lines.Count, matchedLines, closed.Count, rejected);
        return new ClosureBatchResponse { Lines = lines.Count, MatchedLines = matchedLines, ClosedWays = closed.Count };
    }

    private static bool IsCarriageway(string? roadType) =>
        roadType is "motorway" or "motorway_link" or "trunk" or "trunk_link";

    /// <summary>Driving direction of the line around the matched point range.</summary>
    private static double LineBearing(List<double[]> pts, int from, int to)
    {
        int a = Math.Clamp(Math.Min(from, to) - 1, 0, pts.Count - 1);
        int b = Math.Clamp(Math.Max(from, to) + 1, 0, pts.Count - 1);
        if (a == b) { a = Math.Max(0, b - 1); b = Math.Min(pts.Count - 1, a + 1); }
        return Bearing(pts[a][0], pts[a][1], pts[b][0], pts[b][1]);
    }

    private static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var dy = lat2 - lat1;
        var dx = (lon2 - lon1) * Math.Cos((lat1 + lat2) * Math.PI / 360);
        return (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
    }

    private static double AngleDiff(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Overall direction (start to end) and length of each way, from the OSM geometry.</summary>
    private async Task<Dictionary<long, (double Bearing, double LengthM)>> WayBearingsAsync(long[] ids, CancellationToken ct)
    {
        var result = new Dictionary<long, (double, double)>(ids.Length);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT "OsmWayId",
                   degrees(ST_Azimuth(ST_StartPoint("Geometry"), ST_EndPoint("Geometry"))),
                   ST_Length("Geometry"::geography)
            FROM "WayGeometries" WHERE "OsmWayId" = ANY(@ids) AND "Geometry" IS NOT NULL
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(1))
                result[reader.GetInt64(0)] = (reader.GetDouble(1), reader.GetDouble(2));
        return result;
    }

    private async Task UpsertAsync(Dictionary<long, (DateTime Expires, string Source)> closed, DateTime now, CancellationToken ct)
    {
        var ids = closed.Keys.Order().ToArray();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "WayClosures" ("OsmWayId", "FirstReportedAt", "LastReportedAt", "ExpiresAt", "Source")
            SELECT id, @now, @now, exp, src FROM unnest(@ids, @exp, @src) AS v(id, exp, src)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "LastReportedAt"  = EXCLUDED."LastReportedAt",
                "ExpiresAt"       = GREATEST("WayClosures"."ExpiresAt", EXCLUDED."ExpiresAt"),
                "Source"          = EXCLUDED."Source",
                "FirstReportedAt" = CASE WHEN "WayClosures"."ExpiresAt" < @now
                                         THEN EXCLUDED."FirstReportedAt" ELSE "WayClosures"."FirstReportedAt" END;
            DELETE FROM "WayClosures" WHERE "ExpiresAt" < @now - interval '1 day';
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids });
        cmd.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            { Value = ids.Select(i => closed[i].Expires).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("src", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = ids.Select(i => closed[i].Source).ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
