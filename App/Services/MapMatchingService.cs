using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Stateless client for the OpenTrafficRouting /api/v1/trace/bulk endpoint: matches
/// encoded GPS traces to OSM road segments and parses the results. The speed pipeline
/// (<see cref="OpenTrafficDB.App.Pipeline.SpeedMatchingWorker"/>) builds the traces and
/// turns the results into per-road samples.
/// </summary>
public class MapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<MapMatchingService> logger)
{
    private readonly string _otrBaseUrl = config["OpenTrafficRouting:BaseUrl"] ?? "http://localhost:8002";

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
        double? SpeedLimitKmh,
        short RoadClass,
        long[] WayIds);

    /// <summary>
    /// All OSM ways an edge covers: a compacted (shortcut) edge merges consecutive ways of the same
    /// road and reports them in osm_way_ids; osm_way_id alone is only the first of them.
    /// </summary>
    public static long[] WayIdsOf(TraceEdgeInfo e) =>
        e.OsmWayIds is { Length: > 0 } ids
            ? (ids.Contains(e.OsmWayId) || e.OsmWayId == 0 ? ids : [e.OsmWayId, .. ids])
            : [e.OsmWayId];

    /// <summary>
    /// Road class used to choose which roads the traffic tiles show per zoom level:
    /// 1 motorway/trunk, 2 primary, 3 secondary, 4 tertiary, 5 all other roads.
    /// </summary>
    public static short RoadClassOf(string? roadType) => roadType switch
    {
        "motorway" or "motorway_link" or "trunk" or "trunk_link" => 1,
        "primary" or "primary_link" => 2,
        "secondary" or "secondary_link" => 3,
        "tertiary" or "tertiary_link" => 4,
        _ => 5,
    };

    /// <summary>
    /// Match multiple GPS traces to OSM ways via the OpenTrafficRouting /api/v1/trace/bulk endpoint.
    /// Returns raw TraceResult objects (one per polyline) so callers can access both point matches
    /// and interpolated edges.
    /// </summary>
    public async Task<List<TraceResult?>> MatchTracesBulkAsync(
        IReadOnlyList<string> encodedPolylines,
        CancellationToken ct = default) => await MatchTracesBulkAsync(encodedPolylines, null, ct);

    /// <summary>
    /// Bulk match with an optional set of allowed OSM road types (see <see cref="RoadClassFilter"/>).
    /// </summary>
    public async Task<List<TraceResult?>> MatchTracesBulkAsync(
        IReadOnlyList<string> encodedPolylines,
        IReadOnlyCollection<string>? roadTypes,
        CancellationToken ct = default)
    {
        if (encodedPolylines.Count == 0) return [];

        var request = new
        {
            polylines = encodedPolylines,
            costing = "auto",
            gps_accuracy = 10.0,
            search_radius = 50.0,
            interpolate = true,
            attributes = new[] { "osm_way_id", "road_type", "max_speed_kmh" },
            filters = new { road_types = (roadTypes ?? _allowedRoadTypes).ToArray() }
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
                return Enumerable.Repeat<TraceResult?>(null, encodedPolylines.Count).ToList();
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var wrapper = JsonSerializer.Deserialize<BulkTraceResponse>(body, _jsonOptions);
            var results = wrapper?.Results;

            if (results == null)
                return Enumerable.Repeat<TraceResult?>(null, encodedPolylines.Count).ToList();

            return results.Cast<TraceResult?>().ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTrafficRouting bulk trace failed.");
            return Enumerable.Repeat<TraceResult?>(null, encodedPolylines.Count).ToList();
        }
    }

    /// <summary>
    /// Match a single GPS trace. Delegates to the bulk endpoint with one polyline.
    /// Returns a dictionary mapping observation index to MatchResult (point matches only).
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
        return results.Count > 0 ? ParsePointMatches(results[0]) : [];
    }

    internal static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    internal static string EncodePolyline6(IEnumerable<(double Lat, double Lon, DateTime Time)> coords) =>
        PolylineEncoder.Encode(coords.Select(c => (c.Lat, c.Lon)));

    private static readonly HashSet<string> _excludedRoadTypes =
    [
        "driveway", "parking_aisle", "emergency_access", "drive_through_road",
        "footway", "cycleway", "path", "steps", "pedestrian", "service"
    ];

    private static readonly HashSet<string> _allowedRoadTypes =
    [
        "motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential",
        "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link"
    ];

    internal static bool IsExcludedRoadType(string? roadType) =>
        roadType != null &&
        (_excludedRoadTypes.Contains(roadType) || !_allowedRoadTypes.Contains(roadType));

    /// <summary>
    /// Extracts direct point matches from a TraceResult.
    /// Returns a dictionary mapping original_index → MatchResult.
    /// </summary>
    internal Dictionary<int, MatchResult> ParsePointMatches(TraceResult? result)
    {
        if (result?.MatchedPoints == null || result.Edges == null) return [];

        // Build fallback: point-index → edge info from non-interpolated edge coverage ranges.
        var pointToEdge = new Dictionary<int, (long WayId, int SpeedKmh, string? RoadType, long[]? WayIds)>();
        foreach (var edge in result.Edges)
        {
            if (edge.IsInterpolated) continue;
            for (int i = edge.BeginPointIndex; i <= edge.EndPointIndex; i++)
                pointToEdge[i] = (edge.Edge?.OsmWayId ?? 0, edge.Edge?.MaxSpeedKmh ?? 0, edge.Edge?.RoadType, edge.Edge?.OsmWayIds);
        }

        var results = new Dictionary<int, MatchResult>();
        int badType = 0, noEdge = 0, excluded = 0;

        for (int i = 0; i < result.MatchedPoints.Count; i++)
        {
            var mp = result.MatchedPoints[i];
            if (mp.MatchType != "matched" && mp.MatchType != "interpolated")
            {
                badType++;
                continue;
            }

            var edgeInfo = mp.Edge;
            if ((edgeInfo?.OsmWayId ?? 0) == 0 && pointToEdge.TryGetValue(i, out var re) && re.WayId != 0)
                edgeInfo = new TraceEdgeInfo { OsmWayId = re.WayId, MaxSpeedKmh = re.SpeedKmh, RoadType = re.RoadType, OsmWayIds = re.WayIds };

            if (edgeInfo == null || edgeInfo.OsmWayId == 0) { noEdge++; continue; }

            if (IsExcludedRoadType(edgeInfo.RoadType)) { excluded++; continue; }

            results[mp.OriginalIndex] = new MatchResult(
                OsmWayId: edgeInfo.OsmWayId,
                SpeedLimitKmh: edgeInfo.MaxSpeedKmh > 0 ? edgeInfo.MaxSpeedKmh : null,
                RoadClass: RoadClassOf(edgeInfo.RoadType),
                WayIds: WayIdsOf(edgeInfo));
        }

        if (badType + noEdge + excluded > 0)
            _logParseSkip(logger, result.MatchedPoints.Count, results.Count, badType, noEdge, excluded, null);

        return results;
    }

    // --- OpenTrafficRouting trace response DTOs (mirrors OTR TraceModels) ---

    private class BulkTraceResponse
    {
        [JsonPropertyName("results")]
        public List<TraceResult>? Results { get; set; }
    }

    public class TraceResult
    {
        [JsonPropertyName("edges")]
        public List<TraceRouteEdge>? Edges { get; set; }

        [JsonPropertyName("matched_points")]
        public List<TraceMatchedPoint>? MatchedPoints { get; set; }
    }

    public class TraceRouteEdge
    {
        [JsonPropertyName("begin_point_index")]
        public int BeginPointIndex { get; set; }

        [JsonPropertyName("end_point_index")]
        public int EndPointIndex { get; set; }

        [JsonPropertyName("is_interpolated")]
        public bool IsInterpolated { get; set; }

        [JsonPropertyName("interpolation_references")]
        public List<InterpolationReference>? InterpolationReferences { get; set; }

        [JsonPropertyName("edge")]
        public TraceEdgeInfo? Edge { get; set; }

        [JsonPropertyName("node")]
        public TraceNodeInfo? Node { get; set; }
    }

    public class InterpolationReference
    {
        [JsonPropertyName("point_index")]
        public int PointIndex { get; set; }

        [JsonPropertyName("percentage")]
        public double Percentage { get; set; }
    }

    public class TraceMatchedPoint
    {
        [JsonPropertyName("match_type")]
        public string MatchType { get; set; } = "unmatched";

        [JsonPropertyName("original_index")]
        public int OriginalIndex { get; set; }

        [JsonPropertyName("edge")]
        public TraceEdgeInfo? Edge { get; set; }

        [JsonPropertyName("node")]
        public TraceNodeInfo? Node { get; set; }
    }

    public class TraceEdgeInfo
    {
        [JsonPropertyName("osm_way_id")]
        public long OsmWayId { get; set; }

        [JsonPropertyName("osm_way_ids")]
        public long[]? OsmWayIds { get; set; }

        [JsonPropertyName("max_speed_kmh")]
        public int MaxSpeedKmh { get; set; }

        [JsonPropertyName("road_type")]
        public string? RoadType { get; set; }
    }

    public class TraceNodeInfo
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}
