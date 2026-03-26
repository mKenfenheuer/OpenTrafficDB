using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.RoutingWorker.Services;

/// <summary>
/// Proxies route requests to OpenTrafficRouting, which natively provides
/// traffic-enriched routing with jam detection and delay analysis.
/// </summary>
public class RoutingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<RoutingService> logger)
{
    private readonly string _otrBaseUrl = config["OpenTrafficRouting:BaseUrl"] ?? "http://localhost:8002";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Forward the request to OpenTrafficRouting and return the raw response.
    /// </summary>
    public async Task<(JsonElement? Result, int Status, string? Error)>
        RouteRawAsync(JsonElement clientRequest, CancellationToken ct)
    {
        var request = ApplyDefaults(clientRequest);
        var client = httpClientFactory.CreateClient("otr");
        var body = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{_otrBaseUrl}/api/v1/route", body, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTrafficRouting /route request failed.");
            return (null, 503, "Routing service unavailable.");
        }

        var rawJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OpenTrafficRouting /route returned {Status}: {Body}", response.StatusCode, rawJson);
            return (null, (int)response.StatusCode, rawJson);
        }

        try
        {
            return (JsonDocument.Parse(rawJson).RootElement, 200, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse OpenTrafficRouting /route response.");
            return (null, 502, "Invalid JSON from routing service.");
        }
    }

    /// <summary>
    /// Route with traffic enrichment. OpenTrafficRouting handles traffic analysis
    /// natively, so this forwards directly and returns the enriched response.
    /// </summary>
    public async Task<(JsonElement? Result, int Status, string? Error)>
        RouteWithTrafficAsync(JsonElement clientRequest, CancellationToken ct)
    {
        return await RouteRawAsync(clientRequest, ct);
    }

    /// <summary>
    /// Compute predicted travel times for a route across all time-of-day buckets.
    /// Routes via OpenTrafficRouting for each bucket's midpoint to produce a
    /// full-day travel-time profile.
    /// </summary>
    public async Task<(TravelTimeBucketResponse? Result, int Status, string? Error)>
        RouteTravelTimeBucketsAsync(TravelTimeBucketRequest request, CancellationToken ct)
    {
        if (request.Locations == null || request.Locations.Count < 2)
            return (null, 400, "At least two locations are required.");

        int width = Math.Clamp(request.BucketWidthHours, 1, 12);
        if (24 % width != 0)
            return (null, 400, $"Bucket width must divide 24 evenly (1, 2, 3, 4, 6, 8, 12). Got {request.BucketWidthHours}.");

        // Build locations array
        var locations = new JsonArray();
        foreach (var loc in request.Locations)
            locations.Add(new JsonObject { ["lat"] = loc.Lat, ["lon"] = loc.Lon });

        // Determine target day
        var today = DateTime.UtcNow.Date;
        int targetDow = request.DayOfWeek.HasValue
            ? Math.Clamp(request.DayOfWeek.Value, 0, 6)
            : (int)System.DayOfWeek.Monday;
        int daysUntil = (targetDow - (int)today.DayOfWeek + 7) % 7;
        if (daysUntil == 0) daysUntil = 7;
        var referenceDay = today.AddDays(daysUntil);

        int bucketCount = 24 / width;
        var client = httpClientFactory.CreateClient("otr");

        // Route once without date_time to get the base distance
        var baseReq = new JsonObject
        {
            ["locations"] = JsonNode.Parse(locations.ToJsonString()),
            ["costing"] = "auto",
            ["alternates"] = 0
        };
        var baseReqElement = JsonDocument.Parse(baseReq.ToJsonString()).RootElement;
        var withDefaults = ApplyDefaults(baseReqElement);

        HttpResponseMessage baseResponse;
        try
        {
            baseResponse = await client.PostAsync(
                $"{_otrBaseUrl}/api/v1/route",
                new StringContent(withDefaults.ToJsonString(), Encoding.UTF8, "application/json"),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTrafficRouting /route request failed for bucket analysis.");
            return (null, 503, "Routing service unavailable.");
        }

        var baseJson = await baseResponse.Content.ReadAsStringAsync(ct);
        if (!baseResponse.IsSuccessStatusCode)
            return (null, (int)baseResponse.StatusCode, baseJson);

        ValhallaResponse baseParsed;
        try { baseParsed = JsonSerializer.Deserialize<ValhallaResponse>(baseJson)!; }
        catch (JsonException)
        {
            return (null, 502, "Invalid JSON from routing service.");
        }

        if (baseParsed.Trip?.Legs == null || baseParsed.Trip.Legs.Count == 0)
            return (null, 502, "Routing service returned no route.");

        double distanceKm = baseParsed.Trip.Summary?.Length ?? 0;

        // Route for each bucket midpoint to get traffic-aware travel times
        var bucketTasks = new Task<TravelTimeBucket>[bucketCount];
        using var sem = new SemaphoreSlim(4);

        for (int i = 0; i < bucketCount; i++)
        {
            int startHour = i * width;
            int midHour = startHour + width / 2;
            var departure = referenceDay.AddHours(midHour);

            bucketTasks[i] = GetBucketAsync(
                locations, withDefaults, departure, startHour, width, distanceKm, client, sem, ct);
        }

        await Task.WhenAll(bucketTasks);

        var resp = new TravelTimeBucketResponse
        {
            Locations = request.Locations,
            BucketWidthHours = width,
            DistanceKm = Math.Round(distanceKm, 2),
            Buckets = bucketTasks.Select(t => t.Result).ToList()
        };

        return (resp, 200, null);
    }

    private async Task<TravelTimeBucket> GetBucketAsync(
        JsonArray locations,
        JsonObject defaults,
        DateTime departure,
        int startHour,
        int width,
        double distanceKm,
        HttpClient client,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var req = JsonNode.Parse(defaults.ToJsonString())!.AsObject();
            req["locations"] = JsonNode.Parse(locations.ToJsonString());
            req["date_time"] = new JsonObject
            {
                ["type"] = 1,
                ["value"] = departure.ToString("yyyy-MM-ddTHH:mm")
            };

            using var response = await client.PostAsync(
                $"{_otrBaseUrl}/api/v1/route",
                new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"),
                ct);

            double travelTimeSec = 0;
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var parsed = JsonSerializer.Deserialize<ValhallaResponse>(json);
                travelTimeSec = parsed?.Trip?.Summary?.Time ?? 0;
            }

            double travelTimeMin = travelTimeSec / 60.0;
            double avgSpeed = travelTimeSec > 0 ? distanceKm / travelTimeSec * 3600 : 0;

            return new TravelTimeBucket
            {
                Start = $"{startHour:D2}:00",
                End = startHour + width == 24 ? "24:00" : $"{startHour + width:D2}:00",
                TravelTimeMinutes = Math.Round(travelTimeMin, 1),
                MinTravelTimeMinutes = Math.Round(travelTimeMin, 1),
                MaxTravelTimeMinutes = Math.Round(travelTimeMin, 1),
                FreeflowTimeMinutes = Math.Round(travelTimeMin, 1),
                DelayMinutes = 0,
                AverageSpeedKmh = Math.Round(avgSpeed, 1)
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Bucket route request failed for {Hour}:00.", startHour);
            return new TravelTimeBucket
            {
                Start = $"{startHour:D2}:00",
                End = startHour + width == 24 ? "24:00" : $"{startHour + width:D2}:00"
            };
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Apply server-side defaults for costing options.
    /// Only sets values that are not already present in the request.
    /// </summary>
    private static JsonObject ApplyDefaults(JsonElement source)
    {
        var node = JsonNode.Parse(source.GetRawText())!.AsObject();

        node.TryAdd("shortcuts", false);

        var costing = node["costing"]?.GetValue<string>() ?? "auto";

        if (!node.ContainsKey("costing_options"))
            node["costing_options"] = new JsonObject();

        var costingOptions = node["costing_options"]!.AsObject();
        if (!costingOptions.ContainsKey(costing))
            costingOptions[costing] = new JsonObject();

        var modelOptions = costingOptions[costing]!.AsObject();

        // Speed
        modelOptions.TryAdd("top_speed", 120);
        modelOptions.TryAdd("fixed_speed", 0);

        // Road preferences
        modelOptions.TryAdd("use_highways", 0.4);
        modelOptions.TryAdd("use_living_streets", 0.8);
        modelOptions.TryAdd("use_tolls", (JsonNode)1);
        modelOptions.TryAdd("use_ferry", (JsonNode)1);
        modelOptions.TryAdd("use_tracks", (JsonNode)0);

        // Costs & penalties
        modelOptions.TryAdd("maneuver_penalty", 11);
        modelOptions.TryAdd("country_crossing_penalty", (JsonNode)0);
        modelOptions.TryAdd("country_crossing_cost", 600);
        modelOptions.TryAdd("ferry_cost", 300);
        modelOptions.TryAdd("private_access_penalty", 450);
        modelOptions.TryAdd("destination_only_penalty", 300);
        modelOptions.TryAdd("service_penalty", 15);
        modelOptions.TryAdd("service_factor", (JsonNode)1);
        modelOptions.TryAdd("gate_penalty", 300);
        modelOptions.TryAdd("gate_cost", 30);
        modelOptions.TryAdd("toll_booth_penalty", (JsonNode)0);
        modelOptions.TryAdd("toll_booth_cost", 15);
        modelOptions.TryAdd("closure_factor", (JsonNode)9);

        // Vehicle dimensions
        modelOptions.TryAdd("width", 1.6);
        modelOptions.TryAdd("height", 1.9);

        return node;
    }

    // ── Response models ──────────────────────────────────────────────────────

    private class ValhallaResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("trip")]
        public ValhallaTrip? Trip { get; set; }
    }

    private class ValhallaTrip
    {
        [System.Text.Json.Serialization.JsonPropertyName("legs")]
        public List<ValhallaLeg>? Legs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("summary")]
        public ValhallaSummary? Summary { get; set; }
    }

    private class ValhallaLeg
    {
        [System.Text.Json.Serialization.JsonPropertyName("shape")]
        public string? Shape { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("summary")]
        public ValhallaSummary? Summary { get; set; }
    }

    private class ValhallaSummary
    {
        [System.Text.Json.Serialization.JsonPropertyName("time")]
        public double Time { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("length")]
        public double Length { get; set; }
    }
}
