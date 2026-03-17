using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using H3;
using H3.Model;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.App.Data;
using OpenTrafficDB.App.Models;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Performs server-side map matching of GPS observations to OSM road segments
/// using the Valhalla /trace_attributes API.
///
/// Stable way identity: (OsmWayId, OsmStartNodeId, OsmEndNodeId).
/// These three values uniquely identify a directed road segment in OSM and
/// remain stable across Valhalla tile rebuilds.
/// </summary>
public class ValhallaMapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<ValhallaMapMatchingService> logger)
{
    private readonly string _valhallaBaseUrl = config["Valhalla:BaseUrl"] ?? "http://localhost:8002";

    private static readonly Action<ILogger, int, Exception?> _logMapMatched =
        LoggerMessage.Define<int>(LogLevel.Information, default, "Map-matched {Count} observations to OSM ways");

    /// <summary>Result of matching a single GPS point to an OSM road segment.</summary>
    public record MatchResult(
        long OsmWayId,
        long H3CellIndex,
        double? SpeedLimitKmh);

    /// <summary>
    /// Match a GPS trace to OSM ways via Valhalla trace_attributes.
    /// Returns a map from observation index to its MatchResult.
    /// </summary>
    public async Task<Dictionary<int, MatchResult>> MatchTraceAsync(
        IReadOnlyList<(double Lat, double Lon, DateTime Time)> data,
        CancellationToken ct = default)
    {
        var points = data.ToList();
        if (points.Count == 0) return [];

        if (points.Count == 1)
            points = [points[0], points[0]];

        var request = new
        {
            encoded_polyline = EncodePolyline6(points),
            costing = "auto",
            costing_options = new
            {
                auto = new
                {
                    // Strongly discourage routing through private-access roads during map matching
                    private_access_penalty = 10000
                }
            },
            shape_match = "map_snap",
            trace_options = new { search_radius = 100 },
            filters = new
            {
                attributes = new[]
                {
                    "matched.type",
                    "edge.way_id",
                    "edge.speed",
                    "edge.use",
                    "edge.road_class",
                    "matched.point",
                    "matched.edge_index"
                },
                action = "include"
            }
        };

        var json = JsonSerializer.Serialize(request);
        var client = httpClientFactory.CreateClient("valhalla");
        client.Timeout = Timeout.InfiniteTimeSpan;

        try
        {
            using var response = await client.PostAsync(
                $"{_valhallaBaseUrl}/trace_attributes",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                //var errBody = await response.Content.ReadAsStringAsync(ct);
                //logger.LogWarning("Valhalla trace_attributes {Status}: {Body}", response.StatusCode, errBody);
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TraceAttributesResponse>(body);

            if (result?.MatchedPoints == null || result.Edges == null) return [];

            var results = new Dictionary<int, MatchResult>();
            for (int i = 0; i < result.MatchedPoints.Count; i++)
            {
                var mp = result.MatchedPoints[i];
                if ((mp.Type != "matched" && mp.Type != "interpolated") || mp.EdgeIndex is null or ulong.MaxValue || mp.Lat is null || mp.Lon is null) continue;

                var way = result.Edges.ElementAtOrDefault((int)mp.EdgeIndex.Value);
                if (way?.WayId is null || way.IsExcluded) continue;

                var h3 = H3Index.FromLatLng(new LatLng(mp.Lat.Value, mp.Lon.Value), 9);

                results[i] = new MatchResult(
                    OsmWayId:       way.WayId.Value,
                    H3CellIndex:    (long)(ulong)h3,
                    SpeedLimitKmh:  way.Speed);
            }
            return results;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Valhalla map matching failed.");
            return [];
        }
    }

    /// <summary>
    /// Match unmatched observations in the database.
    /// Groups by TripId, sends each trip trace to Valhalla, writes OSM node IDs and H3 index.
    /// </summary>
    public async Task ProcessUnmatchedAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Load only observations that are unmatched and not already marked aggregated (stale unmatched).
        var unmatched = await db.SpeedObservations
            .Where(o => o.OsmWayId == null && !o.IsAggregated)
            .OrderBy(o => o.Timestamp)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return;

        int updatedCount = 0;
        var staleCutoff = DateTime.UtcNow.AddMinutes(-5);

        foreach (var trip in unmatched.GroupBy(o => o.TripId))
        {
            var sorted = trip.OrderBy(o => o.Timestamp).ToList();
            var segments = SplitTrace(sorted);

            foreach (var segment in segments)
            {
                var matchMap = await MatchTraceAsync(
                    segment.Select(o => (o.Latitude, o.Longitude, o.Timestamp)).ToList(), ct);

                foreach (var (idx, match) in matchMap)
                {
                    if (idx >= segment.Count) continue;
                    var obs = segment[idx];
                    obs.OsmWayId      = match.OsmWayId;
                    obs.H3CellIndex   = match.H3CellIndex;
                    obs.SpeedLimitKmh = match.SpeedLimitKmh;
                    updatedCount++;
                }
            }

            // Mark observations that Valhalla could not match and are older than 5 minutes
            // as aggregated so they are cleaned up by ReaggregateAsync and not retried endlessly.
            foreach (var obs in sorted)
            {
                if (obs.OsmWayId == null && obs.CreatedAt < staleCutoff)
                    obs.IsAggregated = true;
            }
        }

        await db.SaveChangesAsync(ct);
        _logMapMatched(logger, updatedCount, null);
    }

    /// <summary>
    /// Splits a sorted observation list into sub-segments suitable for Valhalla map matching.
    /// A new segment starts when:
    ///   - the distance between consecutive points exceeds 500 m, or
    ///   - the accumulated path length exceeds 20,000 m.
    /// </summary>
    private static List<List<SpeedObservation>> SplitTrace(List<SpeedObservation> sorted)
    {
        const double maxSegmentMeters = 20_000;
        const double maxGapMeters     =    500;

        var segments  = new List<List<SpeedObservation>>();
        var current   = new List<SpeedObservation> ([sorted[0]]);
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

    // --- Valhalla response DTOs ---

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

        /// <summary>Max allowed speed in km/h for this edge, as reported by Valhalla.</summary>
        [JsonPropertyName("speed")]
        public double? Speed { get; set; }

        /// <summary>
        /// Valhalla road-use classification (e.g. "driveway", "parking_aisle", "road", ...).
        /// Driveways and other non-public road uses are excluded from map matching results.
        /// </summary>
        [JsonPropertyName("use")]
        public string? Use { get; set; }

        /// <summary>
        /// Valhalla road class (e.g. "motorway", "primary", "residential", "service_other", ...).
        /// </summary>
        [JsonPropertyName("road_class")]
        public string? RoadClass { get; set; }

        // Valhalla use values to exclude: driveways, parking aisles, emergency/drive-through access.
        private static readonly HashSet<string> _excludedUses = ["driveway", "parking_aisle", "emergency_access", "drive_through_road"];

        // Only these road classes represent publicly-drivable through-roads.
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
        /// <summary>ulong.MaxValue (18446744073709551615) = Valhalla sentinel for "no way matched"</summary>
        [JsonPropertyName("edge_index")]
        public ulong? EdgeIndex { get; set; }

        /// <summary>"matched" | "interpolated" | "unmatched"</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("lat")]
        public double? Lat { get; set; }

        [JsonPropertyName("lon")]
        public double? Lon { get; set; }
    }
}
