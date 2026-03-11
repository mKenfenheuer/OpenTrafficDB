using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using H3;
using H3.Model;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;

namespace OpenTrafficDB.Services;

/// <summary>
/// Performs server-side map matching of GPS observations to OSM way references
/// using the Valhalla /trace_attributes API.
///
/// Stable edge identity: OsmWayId + IsForward (heading-derived direction).
/// ValhallaEdgeId is also captured and cached for Valhalla CSV export, but is
/// not used as the primary key because it changes when Valhalla tiles are rebuilt.
/// </summary>
public class ValhallaMapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<ValhallaMapMatchingService> logger)
{
    private readonly string _valhallaBaseUrl = config["Valhalla:BaseUrl"] ?? "http://localhost:8002";

    /// <summary>
    /// Result of matching a single GPS point to a road edge.
    /// </summary>
    public record MatchResult(
        long OsmWayId,
        bool IsForward,
        long ValhallaEdgeId,
        long H3CellIndex);

    /// <summary>
    /// Match a GPS trace (list of observations for one trip) to OSM edges.
    /// Returns a dictionary mapping observation index to its MatchResult.
    /// </summary>
    public async Task<Dictionary<int, MatchResult>> MatchTraceAsync(
        IReadOnlyList<(double Lat, double Lon, DateTime Time)> data,
        CancellationToken ct = default)
    {
        var points = data.ToList();
        if (points.Count == 0) return [];

        // Valhalla requires at least 2 shape points
        if (points.Count == 1)
            points = [points[0], points[0]];

        var encodedPolyline = EncodePolyline6(points);

        var request = new
        {
            encoded_polyline = encodedPolyline,
            costing = "auto",
            shape_match = "map_snap",
            trace_options = new { search_radius = 25 },
            filters = new
            {
                attributes = new[]
                {
                    "edge.id",
                    "edge.way_id",
                    "edge.begin_heading",
                    "matched.point",
                    "matched.edge_index"
                },
                action = "include"
            }
        };

        var json = JsonSerializer.Serialize(request);
        var client = httpClientFactory.CreateClient("valhalla");

        try
        {
            using var response = await client.PostAsync(
                $"{_valhallaBaseUrl}/trace_attributes",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Valhalla trace_attributes returned {Status}", response.StatusCode);
                var errBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Valhalla trace_attributes body: {Body}", errBody);
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TraceAttributesResponse>(body);

            if (result?.MatchedPoints == null || result.Edges == null) return [];

            var results = new Dictionary<int, MatchResult>();
            for (int i = 0; i < result.MatchedPoints.Count; i++)
            {
                var mp = result.MatchedPoints[i];
                if (mp.EdgeIndex is null || mp.Lat is null || mp.Lon is null) continue;
                var edge = result.Edges.ElementAtOrDefault(mp.EdgeIndex.Value);
                if (edge?.WayId is null || edge.Id is null) continue;

                var isForward = (edge.BeginHeading ?? 0) < 180;
                var h3 = H3Index.FromLatLng(new LatLng(mp.Lat.Value, mp.Lon.Value), 9);

                results[i] = new MatchResult(
                    OsmWayId: edge.WayId.Value,
                    IsForward: isForward,
                    ValhallaEdgeId: edge.Id.Value,
                    H3CellIndex: (long)(ulong)h3);
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
    /// Groups by TripId, sends each trip trace to Valhalla, updates OSM fields and H3 index.
    /// </summary>
    public async Task ProcessUnmatchedAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Fetch observations not yet map-matched (OsmWayId not set), recent first, capped at 5000
        var unmatched = await db.SpeedObservations
            .Where(o => o.OsmWayId == null)
            .OrderBy(o => o.Timestamp)
            .Take(5000)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return;

        var byTrip = unmatched.GroupBy(o => o.TripId);
        int updatedCount = 0;

        foreach (var trip in byTrip)
        {
            var sorted = trip.OrderBy(o => o.Timestamp).ToList();
            var points = sorted.Select(o => (o.Latitude, o.Longitude, o.Timestamp)).ToList();
            var matchMap = await MatchTraceAsync(points, ct);

            foreach (var (idx, match) in matchMap)
            {
                if (idx >= sorted.Count) continue;
                var obs = sorted[idx];
                obs.OsmWayId      = match.OsmWayId;
                obs.IsForward     = match.IsForward;
                obs.H3CellIndex   = match.H3CellIndex;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Map-matched {Count} observations to OSM edges.", updatedCount);
        }
    }

    // Encodes a sequence of lat/lon pairs using Google Polyline encoding at precision 6.
    private static string EncodePolyline6(IEnumerable<(double Lat, double Lon, DateTime Time)> coords)
    {
        var sb = new StringBuilder();
        int prevLat = 0, prevLon = 0;
        foreach (var (lat, lon, _) in coords)
        {
            int iLat = (int)Math.Round(lat * 1e6);
            int iLon = (int)Math.Round(lon * 1e6);
            EncodeValue(sb, iLat - prevLat);
            EncodeValue(sb, iLon - prevLon);
            prevLat = iLat;
            prevLon = iLon;
        }
        return sb.ToString();

        static void EncodeValue(StringBuilder sb, int value)
        {
            int v = value < 0 ? ~(value << 1) : value << 1;
            while (v >= 0x20)
            {
                sb.Append((char)(((v & 0x1f) | 0x20) + 63));
                v >>= 5;
            }
            sb.Append((char)(v + 63));
        }
    }

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
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("way_id")]
        public long? WayId { get; set; }

        /// <summary>Heading at the start of the edge in degrees (0–359)</summary>
        [JsonPropertyName("begin_heading")]
        public double? BeginHeading { get; set; }
    }

    private class MatchedPoint
    {
        [JsonPropertyName("edge_index")]
        public int? EdgeIndex { get; set; }

        [JsonPropertyName("lat")]
        public double? Lat { get; set; }

        [JsonPropertyName("lon")]
        public double? Lon { get; set; }
    }
}
