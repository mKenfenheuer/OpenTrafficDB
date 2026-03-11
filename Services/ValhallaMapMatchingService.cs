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
/// Performs server-side map matching of GPS observations to OSM road segments
/// using the Valhalla /trace_attributes API.
///
/// Stable edge identity: (OsmWayId, OsmStartNodeId, OsmEndNodeId).
/// These three values uniquely identify a directed road segment in OSM and
/// remain stable across Valhalla tile rebuilds.
/// </summary>
public class ValhallaMapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<ValhallaMapMatchingService> logger)
{
    private readonly string _valhallaBaseUrl = config["Valhalla:BaseUrl"] ?? "http://localhost:8002";

    /// <summary>Result of matching a single GPS point to an OSM road segment.</summary>
    public record MatchResult(
        long OsmWayId,
        long OsmStartNodeId,
        long OsmEndNodeId,
        long H3CellIndex);

    /// <summary>
    /// Match a GPS trace to OSM edges via Valhalla trace_attributes.
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
            shape_match = "map_snap",
            trace_options = new { search_radius = 25 },
            filters = new
            {
                attributes = new[]
                {
                    "matched.type",
                    "edge.way_id",
                    "edge.begin_osm_node_id",
                    "edge.end_osm_node_id",
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
                var errBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Valhalla trace_attributes {Status}: {Body}", response.StatusCode, errBody);
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TraceAttributesResponse>(body);

            if (result?.MatchedPoints == null || result.Edges == null) return [];

            var results = new Dictionary<int, MatchResult>();
            for (int i = 0; i < result.MatchedPoints.Count; i++)
            {
                var mp = result.MatchedPoints[i];
                if (mp.Type != "matched" || mp.EdgeIndex is null or ulong.MaxValue || mp.Lat is null || mp.Lon is null) continue;

                var edge = result.Edges.ElementAtOrDefault((int)mp.EdgeIndex.Value);
                if (edge?.WayId is null || edge.StartNodeId == 0 || edge.EndNodeId == 0) continue;

                var h3 = H3Index.FromLatLng(new LatLng(mp.Lat.Value, mp.Lon.Value), 9);

                results[i] = new MatchResult(
                    OsmWayId:      edge.WayId.Value,
                    OsmStartNodeId: edge.StartNodeId,
                    OsmEndNodeId:   edge.EndNodeId,
                    H3CellIndex:   (long)(ulong)h3);
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
        var unmatched = await db.SpeedObservations
            .Where(o => o.OsmWayId == null)
            .OrderBy(o => o.Timestamp)
            .Take(5000)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return;

        int updatedCount = 0;

        foreach (var trip in unmatched.GroupBy(o => o.TripId))
        {
            var sorted = trip.OrderBy(o => o.Timestamp).ToList();
            var matchMap = await MatchTraceAsync(
                sorted.Select(o => (o.Latitude, o.Longitude, o.Timestamp)).ToList(), ct);

            foreach (var (idx, match) in matchMap)
            {
                if (idx >= sorted.Count) continue;
                var obs = sorted[idx];
                obs.OsmWayId       = match.OsmWayId;
                obs.OsmStartNodeId = match.OsmStartNodeId;
                obs.OsmEndNodeId   = match.OsmEndNodeId;
                obs.H3CellIndex    = match.H3CellIndex;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Map-matched {Count} observations to OSM edges.", updatedCount);
        }
    }

    // Google Polyline encoding at precision 6.
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
        [JsonPropertyName("way_id")]
        public long? WayId { get; set; }

        [JsonPropertyName("begin_osm_node_id")]
        public long StartNodeId { get; set; }

        [JsonPropertyName("end_osm_node_id")]
        public long EndNodeId { get; set; }
    }

    private class MatchedPoint
    {
        /// <summary>ulong.MaxValue (18446744073709551615) = Valhalla sentinel for "no edge matched"</summary>
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
