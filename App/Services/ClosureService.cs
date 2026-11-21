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
                    foreach (var e in edges)
                    {
                        if (e.Edge == null || e.Edge.OsmWayId == 0
                            || MapMatchingService.IsExcludedRoadType(e.Edge.RoadType)) continue;
                        foreach (var wayId in MapMatchingService.WayIdsOf(e.Edge))
                        {
                            if (wayId == 0) continue;
                            any = true;
                            if (!closed.TryGetValue(wayId, out var cur) || cur.Expires < expires)
                                closed[wayId] = (expires, source);
                        }
                    }
                    if (any) matchedLines++;
                }
            }
        }

        if (closed.Count > 0)
            await UpsertAsync(closed, now, ct);

        logger.LogInformation("Closures: {Lines} lines, {Matched} matched, {Ways} roads closed.",
            lines.Count, matchedLines, closed.Count);
        return new ClosureBatchResponse { Lines = lines.Count, MatchedLines = matchedLines, ClosedWays = closed.Count };
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
