using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.App.Data;
using OpenTrafficDB.App.Models;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Fetches OSM way shapes from the Overpass API and stores them as
/// Valhalla-encoded polylines alongside bounding boxes for tile generation.
/// </summary>
public class WayGeometryService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<WayGeometryService> logger)
{
    private readonly string _overpassBaseUrl =
        config["Overpass:BaseUrl"] ?? "http://localhost:8097";

    /// <summary>
    /// Fetches geometry for up to <paramref name="batchSize"/> way IDs that have
    /// no entry in WayGeometry yet, and persists them.
    /// Returns the number of ways successfully stored.
    /// </summary>
    public async Task<int> EnrichMissingAsync(
        AppDbContext db,
        int batchSize = 100,
        CancellationToken ct = default)
    {
        // Collect distinct way IDs from both sources
        var fromAggregates = await db.WaySpeedAggregates
            .Select(a => a.OsmWayId)
            .Distinct()
            .ToListAsync(ct);

        var fromObservations = await db.SpeedObservations
            .Where(o => o.OsmWayId != null)
            .Select(o => o.OsmWayId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var allWayIds = fromAggregates.Union(fromObservations).ToHashSet();
        if (allWayIds.Count == 0) return 0;

        // Find which ones we already have
        var existing = await db.WayGeometries
            .Where(g => allWayIds.Contains(g.OsmWayId))
            .Select(g => g.OsmWayId)
            .ToHashSetAsync(ct);

        var missing = allWayIds.Except(existing).Take(batchSize).ToList();
        if (missing.Count == 0) return 0;

        var geometries = await FetchFromOverpassAsync(missing, ct);
        if (geometries.Count == 0) return 0;

        db.WayGeometries.AddRange(geometries);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Stored geometry for {Count} OSM ways", geometries.Count);
        return geometries.Count;
    }

    /// <summary>
    /// Fetch geometry for the given way IDs from Overpass in one request.
    /// Returns WayGeometry entities ready to be saved (Id not set).
    /// </summary>
    public async Task<List<WayGeometry>> FetchFromOverpassAsync(
        IReadOnlyList<long> wayIds,
        CancellationToken ct = default)
    {
        if (wayIds.Count == 0) return [];

        // [out:json];way(id:A,B,C,...);out geom;
        var idList = string.Join(",", wayIds);
        var query = $"[out:json];way(id:{idList});out geom;";

        var client = httpClientFactory.CreateClient("overpass");
        client.Timeout = Timeout.InfiniteTimeSpan;

        HttpResponseMessage response;
        try
        {
            var content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("data", query)
            ]);
            response = await client.PostAsync($"{_overpassBaseUrl}/api/interpreter", content, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Overpass request failed for {Count} ways", wayIds.Count);
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Overpass returned {Status} for {Count} ways: {Body}",
                response.StatusCode, wayIds.Count, body);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        OverpassResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<OverpassResponse>(json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Overpass response");
            return [];
        }

        if (result?.Elements == null) return [];

        var now = DateTime.UtcNow;
        var geometries = new List<WayGeometry>(result.Elements.Count);

        foreach (var el in result.Elements)
        {
            if (el.Type != "way" || el.Geometry == null || el.Geometry.Count == 0) continue;

            var maxspeedRaw = el.Tags != null && el.Tags.TryGetValue("maxspeed", out var ms) ? ms : null;

            geometries.Add(new WayGeometry
            {
                OsmWayId         = el.Id,
                MinLat           = el.Bounds?.MinLat ?? el.Geometry.Min(p => p.Lat),
                MinLon           = el.Bounds?.MinLon ?? el.Geometry.Min(p => p.Lon),
                MaxLat           = el.Bounds?.MaxLat ?? el.Geometry.Max(p => p.Lat),
                MaxLon           = el.Bounds?.MaxLon ?? el.Geometry.Max(p => p.Lon),
                EncodedPolyline  = PolylineEncoder.Encode(el.Geometry.Select(p => (p.Lat, p.Lon))),
                MaxSpeedKmh      = ParseMaxSpeed(maxspeedRaw),
                NodeCount        = el.Geometry.Count,
                FetchedAt        = now
            });
        }

        return geometries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse an OSM maxspeed tag value to km/h.
    /// Handles: numeric ("50"), "XX mph", country defaults ("DE:urban", "DE:rural", etc.), "walk", "none".
    /// Returns null when the value is absent, unknown, or unlimited.
    /// </summary>
    private static double? ParseMaxSpeed(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Plain number in km/h
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var kmh) && kmh > 0)
            return kmh;

        // mph conversion
        if (raw.EndsWith(" mph", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(raw[..^4].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var mph))
            return Math.Round(mph * 1.60934, 1);

        // Country defaults — most common values
        return raw.ToLowerInvariant() switch
        {
            "de:living_street" or "living_street"   => 7,
            "de:urban"   or "fr:urban"   or "urban" => 50,
            "de:rural"   or "fr:rural"   or "rural" => 100,
            "de:motorway"                            => 130,
            "fr:motorway"                            => 130,
            "pl:urban"                               => 50,
            "pl:rural"                               => 90,
            "pl:motorway"                            => 140,
            "it:urban"                               => 50,
            "it:rural"                               => 90,
            "it:motorway"                            => 130,
            "walk"                                   => 7,
            "none" or "unlimited"                    => null,
            _                                        => null
        };
    }

    // ── Overpass response DTOs ────────────────────────────────────────────────

    private class OverpassResponse
    {
        [JsonPropertyName("elements")]
        public List<OverpassElement>? Elements { get; set; }
    }

    private class OverpassElement
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("bounds")]
        public OverpassBounds? Bounds { get; set; }

        [JsonPropertyName("geometry")]
        public List<OverpassNode>? Geometry { get; set; }

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }
    }

    private class OverpassBounds
    {
        [JsonPropertyName("minlat")] public double MinLat { get; set; }
        [JsonPropertyName("minlon")] public double MinLon { get; set; }
        [JsonPropertyName("maxlat")] public double MaxLat { get; set; }
        [JsonPropertyName("maxlon")] public double MaxLon { get; set; }
    }

    private class OverpassNode
    {
        [JsonPropertyName("lat")] public double Lat { get; set; }
        [JsonPropertyName("lon")] public double Lon { get; set; }
    }
}
