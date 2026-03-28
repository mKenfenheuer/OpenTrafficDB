using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.AggregateWorker.Services;

/// <summary>
/// Performs server-side map matching of GPS observations to OSM road segments
/// using the OpenTrafficRouting /api/v1/trace/bulk endpoint.
///
/// The bulk endpoint accepts multiple encoded polylines in a single request,
/// which is more efficient than individual trace calls per trip segment.
/// </summary>
public class MapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<MapMatchingService> logger)
{
    private readonly string _otrBaseUrl = config["OpenTrafficRouting:BaseUrl"] ?? "http://localhost:8002";

    private static readonly Action<ILogger, int, Exception?> _logMapMatched =
        LoggerMessage.Define<int>(LogLevel.Information, default, "Map-matched {Count} observations to OSM ways");

    private static readonly Action<ILogger, int, int, Exception?> _logProcessing =
        LoggerMessage.Define<int, int>(LogLevel.Debug, default, "Map-matching {ObsCount} observations across {TripCount} trips");

    private static readonly Action<ILogger, int, int, Exception?> _logBulkRequest =
        LoggerMessage.Define<int, int>(LogLevel.Debug, default, "Sending bulk trace request: {PolylineCount} polylines from {TripCount} trips");

    private static readonly Action<ILogger, int, int, string, Exception?> _logOtrError =
        LoggerMessage.Define<int, int, string>(LogLevel.Warning, default, "OTR bulk trace returned {StatusCode} for {PolylineCount} polylines: {Body}");

    private static readonly Action<ILogger, int, int, int, int, int, Exception?> _logParseSkip =
        LoggerMessage.Define<int, int, int, int, int>(LogLevel.Debug, default,
            "Trace parse: {Total} points → {Matched} matched, {BadType} bad type, {NoEdge} no edge index, {Excluded} excluded edge");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Result of matching a single GPS point to an OSM road segment.</summary>
    public record MatchResult(
        long OsmWayId,
        double? SpeedLimitKmh);

    /// <summary>
    /// Match multiple GPS traces to OSM ways via the OpenTrafficRouting /api/v1/trace/bulk endpoint.
    /// Each polyline in the input corresponds to one trace segment.
    /// Returns a list of dictionaries (one per polyline), mapping observation index to MatchResult.
    /// </summary>
    public async Task<List<Dictionary<int, MatchResult>>> MatchTracesBulkAsync(
        IReadOnlyList<string> encodedPolylines,
        CancellationToken ct = default)
    {
        if (encodedPolylines.Count == 0) return [];

        var request = new
        {
            polylines = encodedPolylines,
            costing = "auto",
            gps_accuracy = 100.0,
            search_radius = 100.0,
            interpolate = true,
            attributes = new[]
            {
                "edge.way_id",
                "edge.speed",
                "edge.use",
                "edge.road_class",
                "matched.type",
                "matched.point",
                "matched.edge_index"
            },
            filters = new
            {
                min_confidence = 0.5
            }
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var client = httpClientFactory.CreateClient("otr");

        try
        {
            using var response = await client.PostAsync(
                $"{_otrBaseUrl}/api/v1/trace/bulk",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logOtrError(logger, (int)response.StatusCode, encodedPolylines.Count, errorBody, null);
                return Enumerable.Repeat(new Dictionary<int, MatchResult>(), encodedPolylines.Count).ToList();
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var results = JsonSerializer.Deserialize<List<TraceAttributesResponse>>(body);

            if (results == null)
                return Enumerable.Repeat(new Dictionary<int, MatchResult>(), encodedPolylines.Count).ToList();

            return results.Select(ParseTraceResult).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTrafficRouting bulk trace failed.");
            return Enumerable.Repeat(new Dictionary<int, MatchResult>(), encodedPolylines.Count).ToList();
        }
    }

    /// <summary>
    /// Match a single GPS trace. Delegates to the bulk endpoint with one polyline.
    /// </summary>
    public async Task<Dictionary<int, MatchResult>> MatchTraceAsync(
        IReadOnlyList<(double Lat, double Lon, DateTime Time)> data,
        CancellationToken ct = default)
    {
        var points = data.ToList();
        if (points.Count == 0) return [];
        if (points.Count == 1)
            points = [points[0], points[0]];

        var polyline = EncodePolyline6(points);
        var results = await MatchTracesBulkAsync([polyline], ct);
        return results.Count > 0 ? results[0] : [];
    }

    /// <summary>
    /// Match observations across multiple trips using the bulk trace endpoint.
    /// Returns the number of observations matched.
    /// </summary>
    public async Task<MatchJobResult> ProcessRangeAsync(
        string[] tripIds, AppDbContext db, CancellationToken ct = default)
    {
        var unmatched = await db.SpeedObservations
            .Where(o => o.OsmWayId == null && !o.IsAggregated && tripIds.Contains(o.TripId))
            .AsNoTracking()
            .OrderBy(o => o.Timestamp)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return new MatchJobResult(0);

        var staleCutoff = DateTime.UtcNow.AddMinutes(-5);
        var trips = unmatched.GroupBy(o => o.TripId).ToList();
        _logProcessing(logger, unmatched.Count, trips.Count, null);

        // Build all segments across all trips and collect their polylines
        var allSegments = new List<(int TripIndex, int SegmentIndex, List<SpeedObservation> Segment)>();
        var allPolylines = new List<string>();
        var tripSegments = new List<List<SpeedObservation>>[trips.Count];

        for (int i = 0; i < trips.Count; i++)
        {
            var sorted = trips[i].OrderBy(o => o.Timestamp).ToList();
            var segments = SplitTrace(sorted);
            tripSegments[i] = segments;

            for (int si = 0; si < segments.Count; si++)
            {
                var segment = segments[si];
                var points = segment.Select(o => (o.Latitude, o.Longitude, o.Timestamp)).ToList();
                if (points.Count == 1)
                    points = [points[0], points[0]];

                allSegments.Add((i, si, segment));
                allPolylines.Add(EncodePolyline6(points));
            }
        }

        // Batch into chunks of 100 (OTR limit) and merge results
        const int batchSize = 100;
        _logBulkRequest(logger, allPolylines.Count, trips.Count, null);
        var bulkResults = new List<Dictionary<int, MatchResult>>(allPolylines.Count);
        for (int offset = 0; offset < allPolylines.Count; offset += batchSize)
        {
            var batch = allPolylines.GetRange(offset, Math.Min(batchSize, allPolylines.Count - offset));
            var batchResult = await MatchTracesBulkAsync(batch, ct);
            bulkResults.AddRange(batchResult);
        }

        // Collect match results and stale IDs
        var matchedIds = new List<long>();
        var matchedWayIds = new List<long>();
        var matchedLimits = new List<double?>();
        var staleIds = new List<long>();

        // Build per-trip matched ID sets for stale detection
        var tripMatchedIds = new HashSet<long>[trips.Count];
        for (int i = 0; i < trips.Count; i++)
            tripMatchedIds[i] = [];

        for (int bi = 0; bi < allSegments.Count; bi++)
        {
            var (tripIdx, segIdx, segment) = allSegments[bi];
            var matchMap = bi < bulkResults.Count ? bulkResults[bi] : [];

            foreach (var (idx, match) in matchMap)
            {
                if (idx >= segment.Count) continue;
                var obs = segment[idx];
                matchedIds.Add(obs.Id);
                matchedWayIds.Add(match.OsmWayId);
                matchedLimits.Add(match.SpeedLimitKmh);
                tripMatchedIds[tripIdx].Add(obs.Id);
            }
        }

        // Find stale unmatched observations
        for (int i = 0; i < trips.Count; i++)
        {
            foreach (var obs in trips[i])
            {
                if (!tripMatchedIds[i].Contains(obs.Id) && obs.CreatedAt < staleCutoff)
                    staleIds.Add(obs.Id);
            }
        }

        // Bulk UPDATE matched observations using unnest — one round-trip for all rows.
        if (matchedIds.Count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "SpeedObservations" o
                SET    "OsmWayId"      = v.way_id,
                       "SpeedLimitKmh" = v.speed_limit
                FROM   unnest(@ids, @way_ids, @limits) AS v(obs_id, way_id, speed_limit)
                WHERE  o."Id" = v.obs_id
                """,
                new object[]
                {
                    new NpgsqlParameter("ids",     NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = matchedIds.ToArray() },
                    new NpgsqlParameter("way_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = matchedWayIds.ToArray() },
                    new NpgsqlParameter("limits",  NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = matchedLimits.ToArray() },
                },
                ct);
        }

        // Mark stale unmatched observations so they are cleaned up in FinalizeAsync.
        if (staleIds.Count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "SpeedObservations"
                SET    "IsAggregated" = TRUE
                WHERE  "Id" = ANY(@ids)
                """,
                new object[] { new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = staleIds.ToArray() } },
                ct);
        }

        _logMapMatched(logger, matchedIds.Count, null);
        return new MatchJobResult(matchedIds.Count);
    }

    /// <summary>
    /// Splits a sorted observation list into sub-segments suitable for map matching.
    /// A new segment starts when:
    ///   - the distance between consecutive points exceeds 500 m, or
    ///   - the accumulated path length exceeds 20,000 m.
    /// </summary>
    private static List<List<SpeedObservation>> SplitTrace(List<SpeedObservation> sorted)
    {
        const double maxSegmentMeters = 20_000;
        const double maxGapMeters     =    500;

        var segments = new List<List<SpeedObservation>>();
        var current  = new List<SpeedObservation>([sorted[0]]);
        double length = 0;

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            var pt   = sorted[i];
            double d = HaversineMeters(prev.Latitude, prev.Longitude, pt.Latitude, pt.Longitude);

            if (d > maxGapMeters || length + d > maxSegmentMeters)
            {
                segments.Add(current);
                current = new List<SpeedObservation>();
                length  = 0;
            }

            current.Add(pt);
            length += d;
        }

        segments.Add(current);
        return segments;
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string EncodePolyline6(IEnumerable<(double Lat, double Lon, DateTime Time)> coords) =>
        PolylineEncoder.Encode(coords.Select(c => (c.Lat, c.Lon)));

    private Dictionary<int, MatchResult> ParseTraceResult(TraceAttributesResponse? result)
    {
        if (result?.MatchedPoints == null || result.Edges == null) return [];

        var results = new Dictionary<int, MatchResult>();
        int badType = 0, noEdge = 0, excluded = 0;

        for (int i = 0; i < result.MatchedPoints.Count; i++)
        {
            var mp = result.MatchedPoints[i];
            if ((mp.Type != "matched" && mp.Type != "interpolated") ||
                mp.Lat is null || mp.Lon is null)
            {
                badType++;
                continue;
            }
            if (mp.EdgeIndex is null or ulong.MaxValue)
            {
                noEdge++;
                continue;
            }

            var way = result.Edges.ElementAtOrDefault((int)mp.EdgeIndex.Value);
            if (way?.WayId is null || way.IsExcluded)
            {
                excluded++;
                continue;
            }

            results[i] = new MatchResult(
                OsmWayId:      way.WayId.Value,
                SpeedLimitKmh: way.Speed);
        }

        if (badType + noEdge + excluded > 0)
            _logParseSkip(logger, result.MatchedPoints.Count, results.Count, badType, noEdge, excluded, null);

        return results;
    }

    // --- OpenTrafficRouting trace response DTOs ---

    private class TraceAttributesResponse
    {
        [JsonPropertyName("edges")]
        public List<EdgeResult>? Edges { get; set; }

        [JsonPropertyName("matched_points")]
        public List<MatchedPoint>? MatchedPoints { get; set; }
    }

    private class EdgeResult
    {
        [JsonPropertyName("way_id")]
        public long? WayId { get; set; }

        [JsonPropertyName("speed")]
        public double? Speed { get; set; }

        [JsonPropertyName("use")]
        public string? Use { get; set; }

        [JsonPropertyName("road_class")]
        public string? RoadClass { get; set; }

        private static readonly HashSet<string> _excludedUses = ["driveway", "parking_aisle", "emergency_access", "drive_through_road"];

        private static readonly HashSet<string> _allowedRoadClasses =
        [
            "motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential"
        ];

        [JsonIgnore]
        public bool IsExcluded =>
            (Use is not null && _excludedUses.Contains(Use)) ||
            (RoadClass is not null && !_allowedRoadClasses.Contains(RoadClass));
    }

    private class MatchedPoint
    {
        [JsonPropertyName("edge_index")]
        public ulong? EdgeIndex { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("lat")]
        public double? Lat { get; set; }

        [JsonPropertyName("lon")]
        public double? Lon { get; set; }
    }
}
