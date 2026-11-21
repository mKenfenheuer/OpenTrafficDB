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

    private sealed record MatchedEdge(long[] WayIds, string? RoadType, bool Interpolated, int From, int To);

    private const double MaxMergeTailMeters = 1000;
    private const double MinCoverageMeters = 500;   // parallel connectors run within the 25 m buffer for a few hundred metres
    private const double MinCoverageShare = 0.5;
    private const double MinMainRunMeters = 1000;     // motorway closures run from junction to junction

    public async Task<ClosureBatchResponse> SubmitAsync(ClosureBatchRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lines = request.Closures
            .Where(l => l.Points.Count >= 2 && l.Points.All(p => p.Length >= 2
                && p[0] is >= -90 and <= 90 && p[1] is >= -180 and <= 180))
            .ToList();

        // 1. Match every line as one trace.
        var matched = new List<(ClosureLine Line, List<MatchedEdge> Edges)>();
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
                    var edges = (results[j]?.Edges ?? [])
                        .Where(e => e.Edge is { OsmWayId: not 0 } && !MapMatchingService.IsExcludedRoadType(e.Edge.RoadType))
                        .Select(e => new MatchedEdge(MapMatchingService.WayIdsOf(e.Edge!).Where(w => w != 0).ToArray(),
                            e.Edge!.RoadType, e.IsInterpolated, e.BeginPointIndex, e.EndPointIndex))
                        .Where(e => e.WayIds.Length > 0)
                        .ToList();
                    if (edges.Count > 0) matched.Add((chunk[j], edges));
                }
            }
        }

        // 2. Per line: trim merge tails, drop opposite-carriageway matches.
        var info = await WayInfoAsync(matched.SelectMany(m => m.Edges.SelectMany(e => e.WayIds)).Distinct().ToArray(), ct);
        var candidates = new List<(int Line, long WayId, string? RoadType)>();
        int trimmed = 0, opposite = 0;
        for (int li = 0; li < matched.Count; li++)
        {
            var (line, edges) = matched[li];
            var (first, last) = TrimMergeTails(edges, info);
            trimmed += edges.Count - (last - first + 1);
            for (int k = first; k <= last; k++)
            {
                var e = edges[k];
                // A point-matched one-way carriageway must run in the line's direction: at an
                // interchange the end of a line can snap onto the opposite carriageway.
                bool checkDirection = !e.Interpolated && IsCarriageway(e.RoadType);
                double lineBearing = checkDirection ? LineBearing(line.Points, e.From, e.To) : 0;
                foreach (var wayId in e.WayIds)
                {
                    if (checkDirection && info.TryGetValue(wayId, out var wi) && wi.LengthM >= 30
                        && AngleDiff(wi.Bearing, lineBearing) > 100)
                    {
                        opposite++;
                        continue;
                    }
                    candidates.Add((li, wayId, e.RoadType));
                }
            }
        }

        // 3. Close only roads the line actually runs along (not long ways it touches at one end).
        var coverage = await CoverageAsync(matched.Select(m => m.Line).ToList(),
            candidates.Select(c => (c.Line, c.WayId)).ToList(), ct);
        var kept = new List<(int Line, long WayId, string? RoadType)>();
        int uncovered = 0;
        foreach (var cand in candidates.Distinct())
        {
            if (coverage.TryGetValue((cand.Line, cand.WayId), out var c) && c.LengthM >= 1
                && c.CoveredM < MinCoverageMeters && c.CoveredM / c.LengthM < MinCoverageShare)
            {
                uncovered++;
                continue;
            }
            kept.Add(cand);
        }

        // 4. A short closed piece of motorway/trunk main carriageway that joins no other closed main
        //    carriageway is a fragment: TomTom tiles repeat features clipped and simplified in the
        //    neighbouring tile's buffer, so the part of a closed ramp after the merge can arrive
        //    without the ramp itself.
        var shortRuns = kept.Where(k => IsMainRoad(k.RoadType)).GroupBy(k => k.Line)
            .Where(g => g.Sum(k => info.TryGetValue(k.WayId, out var w) ? w.LengthM : 0) < MinMainRunMeters)
            .ToDictionary(g => g.Key, g => g.Select(k => k.WayId).ToArray());
        int fragments = 0;
        if (shortRuns.Count > 0)
        {
            var batchMain = kept.Where(k => IsMainRoad(k.RoadType) && !shortRuns.ContainsKey(k.Line)).ToList();
            var joined = await JoinsClosedMainRoadAsync(shortRuns, batchMain, now, ct);
            foreach (var (li, ways) in shortRuns)
                if (!joined.Contains(li))
                {
                    var drop = ways.ToHashSet();
                    fragments += kept.RemoveAll(k => k.Line == li && drop.Contains(k.WayId));
                }
        }

        var closed = new Dictionary<long, (DateTime Expires, string Source, string? RoadType)>();
        foreach (var (li, wayId, roadType) in kept)
        {
            var line = matched[li].Line;
            var expires = now.AddMinutes(Math.Clamp(line.TtlMinutes, 5, 1440));
            if (!closed.TryGetValue(wayId, out var cur) || cur.Expires < expires)
                closed[wayId] = (expires, line.Source ?? "unknown", roadType);
        }

        if (closed.Count > 0)
            await UpsertAsync(closed, now, ct);

        logger.LogInformation(
            "Closures: {Lines} lines, {Matched} matched, {Ways} roads closed ({Trimmed} merge-tail edges, {Opposite} opposite-direction, {Uncovered} barely covered and {Fragments} fragment roads dropped).",
            lines.Count, matched.Count, closed.Count, trimmed, opposite, uncovered, fragments);
        return new ClosureBatchResponse { Lines = lines.Count, MatchedLines = matched.Count, ClosedWays = closed.Count };
    }

    /// <summary>
    /// Index range of the edges that belong to the closure. A closed ramp is reported up to the next
    /// node of the source network, which lies on the main carriageway after the merge (or before the
    /// diverge): a short motorway/trunk section at the start or end of the line, right after a ramp
    /// or at a sharp change of direction, is the acceleration/deceleration lane, not a closed
    /// carriageway.
    /// </summary>
    private static (int First, int Last) TrimMergeTails(List<MatchedEdge> edges, Dictionary<long, (double Bearing, double LengthM)> info)
    {
        double Bearing(MatchedEdge e, bool atStart) =>
            info.TryGetValue(atStart ? e.WayIds[0] : e.WayIds[^1], out var w) ? w.Bearing : double.NaN;
        double Length(MatchedEdge e) => e.WayIds.Sum(w => info.TryGetValue(w, out var i) ? i.LengthM : 0);
        bool IsMain(MatchedEdge e) => e.RoadType is "motorway" or "trunk";
        bool IsLink(MatchedEdge e) => e.RoadType?.EndsWith("_link") == true;

        int first = 0, last = edges.Count - 1;

        // End: maximal run of main-carriageway edges continuing in one direction.
        int t = last;
        while (t - 1 >= 0 && IsMain(edges[t - 1]) && IsMain(edges[t])
               && AngleDiff(Bearing(edges[t - 1], false), Bearing(edges[t], true)) <= 45) t--;
        if (IsMain(edges[last]) && t > 0)
        {
            var body = edges[t - 1];
            double tailLen = 0; for (int k = t; k <= last; k++) tailLen += Length(edges[k]);
            if (tailLen < MaxMergeTailMeters
                && (IsLink(body) || AngleDiff(Bearing(body, false), Bearing(edges[t], true)) > 60))
                last = t - 1;
        }

        // Start: mirror image (diverge onto a closed exit ramp).
        int h = 0;
        while (h + 1 <= last && IsMain(edges[h + 1]) && IsMain(edges[h])
               && AngleDiff(Bearing(edges[h], false), Bearing(edges[h + 1], true)) <= 45) h++;
        if (IsMain(edges[0]) && h < last)
        {
            var body = edges[h + 1];
            double headLen = 0; for (int k = 0; k <= h; k++) headLen += Length(edges[k]);
            if (headLen < MaxMergeTailMeters
                && (IsLink(body) || AngleDiff(Bearing(edges[h], false), Bearing(body, true)) > 60))
                first = h + 1;
        }
        return (first, last);
    }

    /// <summary>Length of each candidate road and how much of it lies within 25 m of its line.</summary>
    private async Task<Dictionary<(int Line, long WayId), (double CoveredM, double LengthM)>> CoverageAsync(
        List<ClosureLine> lines, List<(int Line, long WayId)> candidates, CancellationToken ct)
    {
        var result = new Dictionary<(int, long), (double, double)>();
        if (candidates.Count == 0) return result;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var usedLines = candidates.Select(c => c.Line).Distinct().ToArray();
        var wkts = usedLines.Select(i => "LINESTRING(" + string.Join(",",
            lines[i].Points.Select(p => p[1].ToString("R", inv) + " " + p[0].ToString("R", inv))) + ")").ToArray();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH l AS (SELECT idx, ST_SetSRID(ST_GeomFromText(wkt), 4326) AS g FROM unnest(@lidx, @lwkt) AS t(idx, wkt)),
                 c AS (SELECT DISTINCT line, way FROM unnest(@cl, @cw) AS t(line, way))
            SELECT c.line, c.way,
                   ST_Length(ST_Intersection(wg."Geometry", ST_Buffer(l.g::geography, 25)::geometry)::geography),
                   ST_Length(wg."Geometry"::geography)
            FROM c JOIN l ON l.idx = c.line
                   JOIN "WayGeometries" wg ON wg."OsmWayId" = c.way
            WHERE wg."Geometry" IS NOT NULL
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("lidx", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = usedLines });
        cmd.Parameters.Add(new NpgsqlParameter("lwkt", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = wkts });
        cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = candidates.Select(c => c.Line).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("cw", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = candidates.Select(c => c.WayId).ToArray() });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[(reader.GetInt32(0), reader.GetInt64(1))] =
                (reader.IsDBNull(2) ? 0 : reader.GetDouble(2), reader.IsDBNull(3) ? 0 : reader.GetDouble(3));
        return result;
    }

    private static bool IsMainRoad(string? roadType) => roadType is "motorway" or "trunk";

    /// <summary>
    /// Short runs (by line index) that continue a closed main carriageway: one closed in this batch
    /// by a longer run, or one already in <c>WayClosures</c>. Continuation means an end node of the
    /// run is the start node of the other way or the other way round (same driving direction).
    /// </summary>
    private async Task<HashSet<int>> JoinsClosedMainRoadAsync(Dictionary<int, long[]> runs,
        List<(int Line, long WayId, string? RoadType)> batchMain, DateTime now, CancellationToken ct)
    {
        var result = new HashSet<int>();
        var runIdx = runs.SelectMany(r => r.Value.Select(_ => r.Key)).ToArray();
        var runWays = runs.SelectMany(r => r.Value).ToArray();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH r AS (SELECT run, way FROM unnest(@rr, @rw) AS t(run, way)),
                 o AS (SELECT way FROM unnest(@ow) AS t(way)
                       UNION
                       SELECT c."OsmWayId" FROM "WayClosures" c
                       WHERE c."ExpiresAt" > @now AND c."RoadType" IN ('motorway', 'trunk'))
            SELECT DISTINCT r.run
            FROM r JOIN "WayGeometries" a ON a."OsmWayId" = r.way
                   CROSS JOIN o
                   JOIN "WayGeometries" b ON b."OsmWayId" = o.way
            WHERE NOT EXISTS (SELECT 1 FROM r r2 WHERE r2.run = r.run AND r2.way = o.way)
              AND a."Geometry" IS NOT NULL AND b."Geometry" IS NOT NULL
              AND (ST_DWithin(ST_StartPoint(a."Geometry"), ST_EndPoint(b."Geometry"), 0.00002)
                OR ST_DWithin(ST_EndPoint(a."Geometry"), ST_StartPoint(b."Geometry"), 0.00002))
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("rr", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = runIdx });
        cmd.Parameters.Add(new NpgsqlParameter("rw", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = runWays });
        cmd.Parameters.Add(new NpgsqlParameter("ow", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            { Value = batchMain.Select(b => b.WayId).Distinct().ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt32(0));
        return result;
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
    private async Task<Dictionary<long, (double Bearing, double LengthM)>> WayInfoAsync(long[] ids, CancellationToken ct)
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

    private async Task UpsertAsync(Dictionary<long, (DateTime Expires, string Source, string? RoadType)> closed, DateTime now, CancellationToken ct)
    {
        var ids = closed.Keys.Order().ToArray();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "WayClosures" ("OsmWayId", "FirstReportedAt", "LastReportedAt", "ExpiresAt", "Source", "RoadType")
            SELECT id, @now, @now, exp, src, rt FROM unnest(@ids, @exp, @src, @rt) AS v(id, exp, src, rt)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "LastReportedAt"  = EXCLUDED."LastReportedAt",
                "ExpiresAt"       = GREATEST("WayClosures"."ExpiresAt", EXCLUDED."ExpiresAt"),
                "Source"          = EXCLUDED."Source",
                "RoadType"        = EXCLUDED."RoadType",
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
        cmd.Parameters.Add(new NpgsqlParameter("rt", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = ids.Select(i => closed[i].RoadType).ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
