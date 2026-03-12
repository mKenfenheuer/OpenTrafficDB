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
            shape_match = "map_snap",
            trace_options = new { search_radius = 25 },
            filters = new
            {
                attributes = new[]
                {
                    "matched.type",
                    "edge.way_id",
                    "edge.speed",
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

                var way = result.Edges.ElementAtOrDefault((int)mp.EdgeIndex.Value);
                if (way?.WayId is null) continue;

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
            .Take(5000)
            .ToListAsync(ct);

        if (unmatched.Count == 0) return;

        int updatedCount = 0;
        var staleCutoff = DateTime.UtcNow.AddMinutes(-5);

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
                obs.H3CellIndex    = match.H3CellIndex;
                obs.SpeedLimitKmh  = match.SpeedLimitKmh;
                updatedCount++;
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
