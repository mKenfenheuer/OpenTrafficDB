using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Thin client for OpenTrafficRouting's /api/v1/trace/localize/bulk endpoint.
///
/// /trace/localize/bulk snaps a list of GPS points to the nearest OSM edge while
/// respecting the point's heading. One bulk request handles all points that need
/// (re-)matching in a single aggregation cycle.
/// </summary>
public class HazardMapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<HazardMapMatchingService> logger)
{
    private readonly string _otrBaseUrl = config["OpenTrafficRouting:BaseUrl"] ?? "http://localhost:8002";

    /// <summary>
    /// Default search radius (meters) per-point sent to OTR. Points further than
    /// this from any OSM edge return matched=false.
    /// </summary>
    private int DefaultSearchRadiusMeters =>
        int.TryParse(config["Hazards:MatchSearchRadiusMeters"], out var r) ? r : 100;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Point to localize.</summary>
    public record LocalizePoint(double Lat, double Lon, float Heading);

    /// <summary>Map-match outcome for a single point. Null fields when <see cref="Matched"/> is false.</summary>
    public record LocalizeResult(
        bool Matched,
        double? SnapLat,
        double? SnapLon,
        double? DistanceFromInput,
        long? OsmWayId);

    /// <summary>
    /// Localize a batch of points. Returns one result per input point, same order.
    /// On transport / parse failure every result is <c>Matched = false</c>.
    /// </summary>
    public async Task<List<LocalizeResult>> LocalizeBulkAsync(
        IReadOnlyList<LocalizePoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return [];

        var body = new
        {
            points = points.Select(p => new
            {
                lat            = p.Lat,
                lon            = p.Lon,
                heading        = p.Heading,
                search_radius  = DefaultSearchRadiusMeters
            }).ToArray(),
            search_radius = DefaultSearchRadiusMeters
        };

        var json = JsonSerializer.Serialize(body, _jsonOptions);
        var client = httpClientFactory.CreateClient("otr");

        try
        {
            using var response = await client.PostAsync(
                $"{_otrBaseUrl}/api/v1/trace/localize/bulk",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "OTR /trace/localize/bulk returned {Status} for {Count} points: {Body}",
                    (int)response.StatusCode, points.Count, errorBody);
                return [.. Enumerable.Repeat(new LocalizeResult(false, null, null, null, null), points.Count)];
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<LocalizeBulkResponse>(responseBody, _jsonOptions);
            var results = parsed?.Results;

            if (results == null || results.Count != points.Count)
            {
                logger.LogWarning(
                    "OTR /trace/localize/bulk returned {Returned} results for {Expected} input points — padding with unmatched.",
                    results?.Count ?? 0, points.Count);
                return [.. Enumerable.Repeat(new LocalizeResult(false, null, null, null, null), points.Count)];
            }

            return [.. results.Select(r => new LocalizeResult(
                r.Matched,
                r.Matched ? r.Lat : null,
                r.Matched ? r.Lon : null,
                r.Matched ? r.DistanceFromInput : null,
                r.Matched ? r.Edge?.OsmWayId : null))];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OTR /trace/localize/bulk request failed.");
            return [.. Enumerable.Repeat(new LocalizeResult(false, null, null, null, null), points.Count)];
        }
    }

    // ── Response DTOs (snake_case maps to property names via _jsonOptions) ──────

    private sealed class LocalizeBulkResponse
    {
        public List<LocalizePointResult>? Results { get; set; }
    }

    private sealed class LocalizePointResult
    {
        public bool Matched { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double DistanceFromInput { get; set; }
        public EdgeInfo? Edge { get; set; }
    }

    private sealed class EdgeInfo
    {
        public long OsmWayId { get; set; }
    }
}
