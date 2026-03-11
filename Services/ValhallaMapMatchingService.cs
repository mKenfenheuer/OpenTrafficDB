using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;

namespace OpenTrafficDB.Services;

/// <summary>
/// Performs server-side map matching of GPS observations to Valhalla edge IDs
/// using the Valhalla /trace_attributes API.
/// </summary>
public class ValhallaMapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<ValhallaMapMatchingService> logger)
{
    private readonly string _valhallaBaseUrl = config["Valhalla:BaseUrl"] ?? "http://localhost:8002";

    /// <summary>
    /// Match a GPS trace (list of observations for one trip) to Valhalla edges.
    /// Returns a dictionary mapping observation index to matched edge ID.
    /// </summary>
    public async Task<Dictionary<int, long>> MatchTraceAsync(
        IReadOnlyList<(double Lat, double Lon, DateTime Time)> data,
        CancellationToken ct = default)
    {
        var points = new List<dynamic>(data.Select(p => new { p.Lat, p.Lon, p.Time}));
        if (points.Count == 0) return [];

        var baseTime = points[0].Time;
        if (points.Count == 1)
        {
            points.Add(points[0]);
        }

        var encodedPolyline = EncodePolyline6(points);

        var request = new
        {
            encoded_polyline = encodedPolyline,
            costing = "auto",
            shape_match = "map_snap",
            trace_options = new
            {
                search_radius = 25,
            },
            filters = new
            {
                attributes = new[] { "edge.id","edge.speed","edge.vehicle_type","matched.point", "matched.edge_index" },
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
                var dataString = await response.Content.ReadAsStringAsync();
                logger.LogWarning("Valhalla trace_attributes returned {data}", dataString);
                return [];
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TraceAttributesResponse>(body);

            if (result?.MatchedPoints == null || result.Edges == null) return [];

            var edgeMap = new Dictionary<int, long>();
            for (int i = 0; i < result.MatchedPoints.Count; i++)
            {
                var mp = result.MatchedPoints[i];
                if (mp.EdgeIndex is null) continue;
                var edge = result.Edges.ElementAtOrDefault(mp.EdgeIndex.Value);
                if (edge?.Id is not null)
                    edgeMap[i] = edge.Id.Value;
            }
            return edgeMap;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Valhalla map matching failed.");
            return [];
        }
    }

    /// <summary>
    /// Match unmatched observations in the database.
    /// Groups by TripId, sends each trip trace to Valhalla, updates ValhallaEdgeId.
    /// </summary>
    public async Task ProcessUnmatchedAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Fetch unmatched observations (no edge ID yet), recent ones first, capped at 5000
        var unmatched = await db.SpeedObservations
            .Where(o => o.ValhallaEdgeId == null)
            .OrderBy(o => o.Timestamp)
            .Take(5000)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return;

        // Group by TripId to form GPS traces
        var byTrip = unmatched.GroupBy(o => o.TripId);
        int updatedCount = 0;

        foreach (var trip in byTrip)
        {
            var sorted = trip.OrderBy(o => o.Timestamp).ToList();
            var points = sorted.Select(o => (o.Latitude, o.Longitude, o.Timestamp)).ToList();
            var edgeMap = await MatchTraceAsync(points, ct);

            foreach (var (idx, edgeId) in edgeMap)
            {
                if (idx < sorted.Count)
                {
                    sorted[idx].ValhallaEdgeId = edgeId;
                    updatedCount++;
                }
            }
        }

        if (updatedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Map-matched {Count} observations to Valhalla edges.", updatedCount);
        }
    }

    // Encodes a sequence of lat/lon pairs using Google Polyline encoding at precision 6.
    // Adapted from https://github.com/DennisOSRM/Project-OSRM-Web OSRM.RoutingGeometry.js
    private static string EncodePolyline6(IEnumerable<dynamic> coords)
    {
        var sb = new StringBuilder();
        int prevLat = 0, prevLon = 0;
        foreach (var coord in coords)
        {
            int iLat = (int)Math.Round(coord.Lat * 1e6);
            int iLon = (int)Math.Round(coord.Lon * 1e6);
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
    }

    private class MatchedPoint
    {
        [JsonPropertyName("edge_index")]
        public int? EdgeIndex { get; set; }
    }
}
