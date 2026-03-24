using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.RoutingWorker.Services;

/// <summary>
/// Proxies Valhalla /route requests and enriches the response with
/// per-route traffic delay analysis using the OpenTrafficDB speed data.
///
/// Strategy per route:
///   1. Decode each leg shape (precision-6 polyline) independently so that
///      Valhalla's per-leg shape indices remain valid.
///   2. Run each leg shape through Valhalla /trace_attributes (edge_walk)
///      to obtain per-edge (way_id, length_km, matched shape-point indices).
///   3. Query WaySpeedSummary for all way IDs in one DB round-trip.
///   4. For each edge compute traffic delay (live vs free-flow).
///      Distribute the delay evenly across the edge's matched shape points.
///   5. Patch maneuver.time by summing delays for shape indices in the
///      maneuver's [begin_shape_index, end_shape_index] range.
///      Recalculate leg and trip summary times accordingly.
///   6. Group consecutive congested edges into traffic-jam segments.
///   7. For significant jams on the best candidate, request an additional
///      Valhalla route with those jam areas excluded (exit/rejoin strategy).
///      Merge the detour into the candidate pool and re-rank.
/// </summary>
public class ValhallaRoutingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<ValhallaRoutingService> logger)
{
    private readonly string _valhallaBaseUrl = config["Valhalla:BaseUrl"] ?? "http://localhost:8002";

    private const double JamThreshold      = 0.95;
    private const double MinJamDelaySeconds = 10.0;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions _camelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Forward the request to Valhalla and return the raw response without traffic enrichment.
    /// </summary>
    public async Task<(JsonElement? Result, int ValhallaStatus, string? ValhallaError)>
        RouteRawAsync(JsonElement clientRequest, CancellationToken ct)
    {
        var request = ApplyDefaults(clientRequest);
        var client  = httpClientFactory.CreateClient("valhalla");
        var body    = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{_valhallaBaseUrl}/route", body, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Valhalla /route request failed.");
            return (null, 503, "Valhalla routing service unavailable.");
        }

        var rawJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Valhalla /route returned {Status}: {Body}", response.StatusCode, rawJson);
            return (null, (int)response.StatusCode, rawJson);
        }

        try
        {
            return (JsonDocument.Parse(rawJson).RootElement, 200, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Valhalla /route response.");
            return (null, 502, "Invalid JSON from Valhalla.");
        }
    }

    public async Task<(JsonElement? Result, int ValhallaStatus, string? ValhallaError)>
        RouteWithTrafficAsync(JsonElement clientRequest, CancellationToken ct)
    {
        var withDefaults = ApplyDefaults(clientRequest);
        var defaultsElement = JsonDocument.Parse(withDefaults.ToJsonString()).RootElement;

        int userAlternates  = ReadAlternates(defaultsElement);
        int requestedRoutes = userAlternates + 1;

        // Extract requested departure time for historical speed lookup
        var departureUtc = ExtractDepartureTime(defaultsElement);

        // ── Two parallel Valhalla requests ──────────────────────────────────
        var mainRequest = InjectAlternates(defaultsElement, userAlternates);

        var tdRequest = JsonNode.Parse(withDefaults.ToJsonString())!.AsObject();
        tdRequest["alternates"] = 0;
        tdRequest["date_time"]  = new JsonObject { ["type"] = 0 };

        var client = httpClientFactory.CreateClient("valhalla");

        var mainTask = client.PostAsync($"{_valhallaBaseUrl}/route",
            new StringContent(mainRequest.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var tdTask   = client.PostAsync($"{_valhallaBaseUrl}/route",
            new StringContent(tdRequest.ToJsonString(), Encoding.UTF8, "application/json"), ct);

        HttpResponseMessage mainResponse;
        try { mainResponse = await mainTask; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Valhalla /route request failed.");
            return (null, 503, "Valhalla routing service unavailable.");
        }

        var rawJson = await mainResponse.Content.ReadAsStringAsync(ct);

        if (!mainResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Valhalla /route returned {Status}: {Body}", mainResponse.StatusCode, rawJson);
            return (null, (int)mainResponse.StatusCode, rawJson);
        }

        // ── Parse into typed models and collect all trips ────────────────────
        ValhallaResponse mainParsed;
        try { mainParsed = JsonSerializer.Deserialize<ValhallaResponse>(rawJson)!; }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Valhalla /route response.");
            return (null, 502, "Invalid JSON from Valhalla.");
        }

        var allTrips = new List<Trip>();
        if (mainParsed.Trip != null) allTrips.Add(mainParsed.Trip);
        if (mainParsed.Alternates != null)
            allTrips.AddRange(mainParsed.Alternates.Where(a => a.Trip != null).Select(a => a.Trip));

        // Merge the time-dependent route
        try
        {
            var tdResponse = await tdTask;
            if (tdResponse.IsSuccessStatusCode)
            {
                var tdJson = await tdResponse.Content.ReadAsStringAsync(ct);
                var tdParsed = JsonSerializer.Deserialize<ValhallaResponse>(tdJson);
                if (tdParsed?.Trip != null) allTrips.Add(tdParsed.Trip);
                if (tdParsed?.Alternates != null)
                    allTrips.AddRange(tdParsed.Alternates.Where(a => a.Trip != null).Select(a => a.Trip));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Time-dependent /route request failed (non-fatal).");
        }

        // ── Deduplicate by combined leg shapes ──────────────────────────────
        var uniqueTrips = new List<Trip>();
        foreach (var trip in allTrips)
        {
            if (trip.Legs == null || trip.Legs.Count == 0) continue;
            if (!uniqueTrips.Any(u => u.Equals(trip)))
                uniqueTrips.Add(trip);
        }

        // ── Analyse traffic for every unique trip in parallel ────────────────
        var analysisResults = new RouteAnalysisResult[uniqueTrips.Count];
        using var sem = new SemaphoreSlim(4);
        await Task.WhenAll(uniqueTrips.Select(async (trip, i) =>
        {
            var legShapes = trip.Legs
                .Where(l => !string.IsNullOrEmpty(l.Shape))
                .Select(l => PolylineEncoder.Decode(l.Shape).ToList())
                .ToList();

            await sem.WaitAsync(ct);
            try   { analysisResults[i] = await AnalyseRouteAsync(i, legShapes, departureUtc, ct); }
            finally { sem.Release(); }
        }));

        // ── Build pool, rank by composite score (70% time, 30% eco) ─────────
        var candidates = uniqueTrips
            .Select((trip, i) => (Trip: trip, Result: analysisResults[i]))
            .Where(x => x.Result is not null)
            .ToList();

        var pool = RankByComposite(candidates);

        var finalRanked = pool.Take(requestedRoutes).ToList();

        // ── Try jam-avoiding detours if any route has a jam > 5 min ─────────
        bool hasSignificantJam = finalRanked.Any(r =>
            r.Result.Analysis.Jams.Any(j => j.DelayMinutes >= MinDetourJamDelayMinutes));

        if (hasSignificantJam)
        {
            var detourTasks = finalRanked
                .Where(r => r.Result.Analysis.Jams.Any(j => j.DelayMinutes >= MinDetourJamDelayMinutes))
                .Select(r => TryJamDetourAsync(clientRequest, r.Result.Analysis.Jams, departureUtc, ct));
            var allDetours = await Task.WhenAll(detourTasks);

            // Convert detour results to (Trip, RouteAnalysisResult) and merge
            foreach (var detourList in allDetours)
            {
                foreach (var (tripNode, result) in detourList)
                {
                    // Parse the detour trip node back into a typed Trip
                    var detourTrip = JsonSerializer.Deserialize<Trip>(tripNode.ToJsonString());
                    if (detourTrip?.Legs == null || detourTrip.Legs.Count == 0) continue;
                    // Only add if not too similar to an existing route (>=90% shape overlap)
                    if (!pool.Any(p => p.Trip.Equals(detourTrip)))
                        pool.Add((detourTrip, result));
                }
            }

            // Re-rank the full pool
            pool = RankByComposite(pool);
            finalRanked = pool.Take(requestedRoutes).ToList();
        }

        for (int i = 0; i < finalRanked.Count; i++)
            finalRanked[i].Result.Analysis.RouteIndex = i;

        // ── Rebuild JSON response ───────────────────────────────────────────
        // Convert typed trips back to JsonNode for response assembly
        var rankedForResponse = finalRanked.Select(r =>
        {
            var tripNode = JsonNode.Parse(JsonSerializer.Serialize(r.Trip))!;
            return (TripNode: tripNode, r.Result);
        }).ToList();

        var valhallaDoc = JsonDocument.Parse(rawJson).RootElement;
        var rebuilt = RebuildValhallaResponse(valhallaDoc, rankedForResponse);
        return (rebuilt, 200, null);
    }

    // ── Travel-time buckets for a whole day ─────────────────────────────────

    /// <summary>
    /// Compute predicted travel times for a route across all time-of-day buckets.
    /// Routes once via Valhalla, then runs the traffic analysis for each bucket's
    /// midpoint to produce a full-day travel-time profile.
    /// </summary>
    public async Task<(TravelTimeBucketResponse? Result, int Status, string? Error)>
        RouteTravelTimeBucketsAsync(TravelTimeBucketRequest request, CancellationToken ct)
    {
        if (request.Locations == null || request.Locations.Count < 2)
            return (null, 400, "At least two locations are required.");

        int width = Math.Clamp(request.BucketWidthHours, 1, 12);
        if (24 % width != 0)
            return (null, 400, $"Bucket width must divide 24 evenly (1, 2, 3, 4, 6, 8, 12). Got {request.BucketWidthHours}.");

        // Build a Valhalla route request from the locations
        var locations = new JsonArray();
        foreach (var loc in request.Locations)
            locations.Add(new JsonObject { ["lat"] = loc.Lat, ["lon"] = loc.Lon });

        var valhallaReq = new JsonObject
        {
            ["locations"]  = locations,
            ["costing"]    = "auto",
            ["alternates"] = 0
        };

        var reqElement = JsonDocument.Parse(valhallaReq.ToJsonString()).RootElement;
        var withDefaults = ApplyDefaults(reqElement);

        // Single Valhalla route to get the shape
        var client = httpClientFactory.CreateClient("valhalla");
        var body   = new StringContent(withDefaults.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await client.PostAsync($"{_valhallaBaseUrl}/route", body, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Valhalla /route request failed for bucket analysis.");
            return (null, 503, "Valhalla routing service unavailable.");
        }

        var rawJson = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return (null, (int)response.StatusCode, rawJson);

        ValhallaResponse parsed;
        try { parsed = JsonSerializer.Deserialize<ValhallaResponse>(rawJson)!; }
        catch (JsonException)
        {
            return (null, 502, "Invalid JSON from Valhalla.");
        }

        if (parsed.Trip?.Legs == null || parsed.Trip.Legs.Count == 0)
            return (null, 502, "Valhalla returned no route.");

        var legShapes = ExtractLegShapes(parsed.Trip);
        double distanceKm = parsed.Trip.Summary?.Length ?? 0;

        // Run traffic analysis for each bucket's midpoint (using next Monday as reference)
        int bucketCount = 24 / width;
        var today = DateTime.UtcNow.Date;
        // Use next Monday to get a representative weekday
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        var referenceDay = today.AddDays(daysUntilMonday);

        var buckets = new TravelTimeBucket[bucketCount];
        using var sem = new SemaphoreSlim(4);

        await Task.WhenAll(Enumerable.Range(0, bucketCount).Select(async i =>
        {
            int startHour = i * width;
            int midHour   = startHour + width / 2;
            int midMinute = (width % 2 == 1) ? 30 : 0;
            var departure = referenceDay.AddHours(midHour).AddMinutes(midMinute);

            await sem.WaitAsync(ct);
            try
            {
                var result = await AnalyseRouteAsync(i, legShapes, departure, ct);
                buckets[i] = new TravelTimeBucket
                {
                    Start              = $"{startHour:D2}:00",
                    End                = startHour + width == 24 ? "24:00" : $"{startHour + width:D2}:00",
                    TravelTimeMinutes  = Math.Round(result.Analysis.LiveDurationMinutes, 1),
                    FreeflowTimeMinutes = Math.Round(result.Analysis.FreeflowDurationMinutes, 1),
                    DelayMinutes       = Math.Round(result.Analysis.TotalDelayMinutes, 1),
                    AverageSpeedKmh    = Math.Round(result.Analysis.AverageSpeedKmh, 1)
                };
            }
            finally { sem.Release(); }
        }));

        var resp = new TravelTimeBucketResponse
        {
            Locations       = request.Locations,
            BucketWidthHours = width,
            DistanceKm       = Math.Round(distanceKm, 2),
            Buckets          = buckets.ToList()
        };

        return (resp, 200, null);
    }

    // ── Shape extraction ─────────────────────────────────────────────────────

    /// <summary>
    /// Extract decoded leg shapes from a typed Trip object.
    /// Returns [legIndex] → list of (Lat, Lon) coords.
    /// </summary>
    private static List<List<(double Lat, double Lon)>> ExtractLegShapes(Trip trip)
    {
        if (trip.Legs == null) return [];
        return trip.Legs
            .Where(l => !string.IsNullOrEmpty(l.Shape))
            .Select(l => PolylineEncoder.Decode(l.Shape).ToList())
            .ToList();
    }

    // ── Per-route traffic analysis ────────────────────────────────────────────

    private async Task<RouteAnalysisResult> AnalyseRouteAsync(
        int routeIndex,
        List<List<(double Lat, double Lon)>> legShapes,
        DateTime? departureUtc,
        CancellationToken ct)
    {
        if (legShapes.Count == 0 || legShapes.All(l => l.Count < 2))
            return new RouteAnalysisResult(new RouteTrafficAnalysis { RouteIndex = routeIndex }, []);

        // Separate trace_attributes call per leg to keep shape indices intact
        var legEdges = new List<List<EdgeData>>(legShapes.Count);
        foreach (var legCoords in legShapes)
            legEdges.Add(legCoords.Count >= 2 ? await GetEdgesAsync(legCoords, ct) : []);

        // Single DB query across all legs — own context per concurrent task
        var wayIds = legEdges
            .SelectMany(edges => edges.Select(e => e.WayId))
            .Where(id => id != 0).Distinct().ToList();

        bool isLive = departureUtc is null; // null = depart now, use live speeds

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ── Live mode: summaries with freshness check ────────────────────────
        Dictionary<long, WaySpeedSummary>? summaries = null;
        if (isLive)
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            summaries = await db.WaySpeedSummaries
                .AsNoTracking()
                .Where(s => wayIds.Contains(s.OsmWayId))
                .ToDictionaryAsync(s => s.OsmWayId, ct);
            // Mark stale entries by nulling ConstrainedSpeed in-memory
            foreach (var kvp in summaries)
                if (kvp.Value.LastUpdated < cutoff)
                    kvp.Value.ConstrainedSpeed = null;
        }

        // ── Predicted mode: load historical speeds for the time window ───────
        // We need all buckets the route might traverse (departure to departure + ~3h max).
        // Keyed by (wayId, dow, bucket) for per-edge lookup as time advances.
        Dictionary<(long WayId, int Dow, int Bucket), double>? historicalByTime = null;
        if (!isLive)
        {
            // Load freeflow baselines (always needed for fallback)
            summaries = await db.WaySpeedSummaries
                .AsNoTracking()
                .Where(s => wayIds.Contains(s.OsmWayId))
                .ToDictionaryAsync(s => s.OsmWayId, ct);

            // Estimate max route duration: sum freeflow edge times
            double estDurationMin = legEdges
                .SelectMany(e => e)
                .Sum(e => {
                    double ff = summaries.TryGetValue(e.WayId, out var s) && s.FreeflowSpeed > 0
                        ? s.FreeflowSpeed : 50;
                    return e.LengthKm / ff * 60;
                });
            // Pad by 50% for traffic
            int windowMinutes = (int)(estDurationMin * 1.5) + 30;

            var dep = departureUtc!.Value;
            int depMinOfDay = dep.Hour * 60 + dep.Minute;
            int startBucket = depMinOfDay / 5;
            int endBucket   = (depMinOfDay + windowMinutes) / 5;

            // Build list of (dow, bucket) pairs — may wrap across midnight / next day
            var dowBuckets = new HashSet<(int Dow, int Bucket)>();
            for (int b = startBucket; b <= endBucket; b++)
            {
                int actualBucket = b % 288; // 288 = 24*60/5
                int dayOffset    = b / 288;
                int dow          = ((int)dep.DayOfWeek + dayOffset) % 7;
                dowBuckets.Add((dow, actualBucket));
            }

            var dows    = dowBuckets.Select(x => x.Dow).Distinct().ToList();
            var buckets = dowBuckets.Select(x => x.Bucket).Distinct().ToList();

            var rawHist = await db.WaySpeedAggregates
                .AsNoTracking()
                .Where(a => wayIds.Contains(a.OsmWayId)
                         && dows.Contains(a.DayOfWeek)
                         && buckets.Contains(a.FiveMinuteBucket)
                         && a.SampleCount > 0)
                .ToListAsync(ct);

            historicalByTime = rawHist.ToDictionary(
                a => (a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket),
                a => a.SumSpeedKmh / a.SampleCount);
        }

        double totalFreeflowSec = 0, totalLiveSec = 0, totalKm = 0, coveredKm = 0;
        var allEdgeInfo = new List<(EdgeData Edge, double FreeflowSpeed, double LiveSpeed, double LengthKm, bool HasLiveData)>();
        var legShapeDelays = new List<double[]>(legShapes.Count);
        double cumulativeSec = 0; // tracks time offset from departure for predicted mode

        for (int li = 0; li < legEdges.Count; li++)
        {
            var (legDelay, edgeInfo, ffSec, livSec, km, covKm) = isLive
                ? ComputeLegDelaysLive(legEdges[li], legShapes[li].Count, summaries!)
                : ComputeLegDelaysPredicted(legEdges[li], legShapes[li].Count,
                    summaries!, historicalByTime!, departureUtc!.Value, cumulativeSec);

            legShapeDelays.Add(legDelay);
            allEdgeInfo.AddRange(edgeInfo);
            totalFreeflowSec += ffSec;
            totalLiveSec     += livSec;
            totalKm          += km;
            coveredKm        += covKm;
            cumulativeSec    += livSec; // advance clock for next leg
        }

        var jams        = DetectJams(allEdgeInfo);
        double ffMin    = totalFreeflowSec / 60.0;
        double liveMin  = totalLiveSec     / 60.0;
        double delayMin = Math.Max(0, liveMin - ffMin);

        // CO2 estimation using speed-dependent emission model for a standard
        // European passenger car: co2_g/km = 50 + 3000/speed + 0.005 * speed²
        // This captures high emissions at low speeds (stop-and-go inefficiency)
        // and at high speeds (aerodynamic drag).
        double totalCo2Grams = 0;
        foreach (var (_, _, live, lengthKm, _) in allEdgeInfo)
        {
            double speed    = Math.Max(live, 5); // floor to avoid division issues
            double co2PerKm = 50.0 + 3000.0 / speed + 0.005 * speed * speed;
            totalCo2Grams  += co2PerKm * lengthKm;
        }

        double distPerMin = liveMin > 0 ? totalKm / liveMin : 0;
        double avgSpeed   = totalLiveSec > 0 ? totalKm / totalLiveSec * 3600 : 0;

        // Eco score (0–100, higher = greener): compares actual CO2/km to the
        // theoretical optimum (~120 g/km at ~55 km/h for a standard car).
        // 100 = optimal efficiency, 0 = extremely inefficient.
        double avgCo2PerKm = totalKm > 0 ? totalCo2Grams / totalKm : 200;
        double optimalCo2  = 120.0; // g/km at ~55 km/h sweet spot
        double ecoScore    = Math.Clamp(100.0 * optimalCo2 / Math.Max(avgCo2PerKm, 1), 0, 100);

        var analysis = new RouteTrafficAnalysis
        {
            RouteIndex              = routeIndex,
            FreeflowDurationMinutes = Math.Round(ffMin,                              2),
            LiveDurationMinutes     = Math.Round(liveMin,                            2),
            TotalDelayMinutes       = Math.Round(delayMin,                           2),
            DelayPercent            = Math.Round(ffMin > 0 ? delayMin / ffMin * 100 : 0, 1),
            TrafficCoverageRatio    = Math.Round(totalKm > 0 ? coveredKm / totalKm  : 0, 3),
            DistanceKm              = Math.Round(totalKm,       2),
            DistancePerMinuteKm     = Math.Round(distPerMin,    3),
            AverageSpeedKmh         = Math.Round(avgSpeed,      1),
            Co2GramsEstimate        = Math.Round(totalCo2Grams, 0),
            EcoScore                = Math.Round(ecoScore,      1),
            Jams                    = jams
        };

        return new RouteAnalysisResult(analysis, legShapeDelays);
    }

    /// <summary>
    /// Compute per-edge speeds for one leg and build the shape-point delay array.
    /// Delay is distributed uniformly across each edge's matched shape points.
    /// </summary>
    /// <summary>
    /// Live mode: use ConstrainedSpeed (already null for stale entries), fall back to freeflow.
    /// </summary>
    private static (
        double[] ShapePointDelays,
        List<(EdgeData, double, double, double, bool)> EdgeInfo,
        double FreeflowSec, double LiveSec, double TotalKm, double CoveredKm)
        ComputeLegDelaysLive(
            List<EdgeData> edges,
            int shapePointCount,
            Dictionary<long, WaySpeedSummary> summaries)
    {
        var shapePointDelays = new double[shapePointCount];
        var edgeInfo = new List<(EdgeData, double, double, double, bool)>();
        double freeflowSec = 0, liveSec = 0, totalKm = 0, coveredKm = 0;

        foreach (var edge in edges)
        {
            double freeflow = 0, live = 0;
            bool hasData = false;

            if (summaries.TryGetValue(edge.WayId, out var s))
            {
                freeflow = s.FreeflowSpeed > 0 ? s.FreeflowSpeed : 1;
                if (s.ConstrainedSpeed.HasValue) // null if stale (>1h)
                {
                    live    = Math.Max(s.ConstrainedSpeed.Value, 1);
                    hasData = true;
                }
                else
                {
                    live    = freeflow;
                }
            }

            if (freeflow <= 0) freeflow = 50;
            if (live     <= 0) live     = freeflow;

            AccumulateEdge(edge, freeflow, live, hasData, shapePointDelays, edgeInfo,
                ref freeflowSec, ref liveSec, ref totalKm, ref coveredKm);
        }

        return (shapePointDelays, edgeInfo, freeflowSec, liveSec, totalKm, coveredKm);
    }

    /// <summary>
    /// Predicted mode: use historical speeds for the correct time bucket,
    /// advancing the clock along the route based on cumulative travel time.
    /// </summary>
    private static (
        double[] ShapePointDelays,
        List<(EdgeData, double, double, double, bool)> EdgeInfo,
        double FreeflowSec, double LiveSec, double TotalKm, double CoveredKm)
        ComputeLegDelaysPredicted(
            List<EdgeData> edges,
            int shapePointCount,
            Dictionary<long, WaySpeedSummary> summaries,
            Dictionary<(long WayId, int Dow, int Bucket), double> historicalByTime,
            DateTime departure,
            double priorCumulativeSec)
    {
        var shapePointDelays = new double[shapePointCount];
        var edgeInfo = new List<(EdgeData, double, double, double, bool)>();
        double freeflowSec = 0, liveSec = 0, totalKm = 0, coveredKm = 0;
        double edgeCumulativeSec = priorCumulativeSec;

        foreach (var edge in edges)
        {
            double freeflow = 0, live = 0;
            bool hasData = false;

            if (summaries.TryGetValue(edge.WayId, out var s))
                freeflow = s.FreeflowSpeed > 0 ? s.FreeflowSpeed : 1;
            if (freeflow <= 0) freeflow = 50;

            // Calculate what time we arrive at this edge
            var edgeTime  = departure.AddSeconds(edgeCumulativeSec);
            int dow       = (int)edgeTime.DayOfWeek;
            int bucket    = (edgeTime.Hour * 60 + edgeTime.Minute) / 5;

            if (historicalByTime.TryGetValue((edge.WayId, dow, bucket), out var hist) && hist > 0)
            {
                live    = hist;
                hasData = true;
            }
            else
            {
                live = freeflow;
            }

            if (live <= 0) live = freeflow;

            AccumulateEdge(edge, freeflow, live, hasData, shapePointDelays, edgeInfo,
                ref freeflowSec, ref liveSec, ref totalKm, ref coveredKm);

            // Advance clock by this edge's travel time
            edgeCumulativeSec += edge.LengthKm / live * 3600;
        }

        return (shapePointDelays, edgeInfo, freeflowSec, liveSec, totalKm, coveredKm);
    }

    /// <summary>Shared edge accumulation logic for both live and predicted modes.</summary>
    private static void AccumulateEdge(
        EdgeData edge, double freeflow, double live, bool hasData,
        double[] shapePointDelays,
        List<(EdgeData, double, double, double, bool)> edgeInfo,
        ref double freeflowSec, ref double liveSec, ref double totalKm, ref double coveredKm)
    {
        double edgeFreeflowSec = edge.LengthKm / freeflow * 3600;
        double edgeLiveSec     = edge.LengthKm / live     * 3600;
        double edgeDelaySec    = edgeLiveSec - edgeFreeflowSec;

        freeflowSec += edgeFreeflowSec;
        liveSec     += edgeLiveSec;
        totalKm     += edge.LengthKm;
        if (hasData) coveredKm += edge.LengthKm;

        edgeInfo.Add((edge, freeflow, live, edge.LengthKm, hasData));

        if (edge.ShapeIndices.Count > 0 && edgeDelaySec > 0)
        {
            double delayPerPoint = edgeDelaySec / edge.ShapeIndices.Count;
            foreach (var idx in edge.ShapeIndices)
                if ((uint)idx < (uint)shapePointDelays.Length)
                    shapePointDelays[idx] += delayPerPoint;
        }
    }

    // ── Valhalla trace_attributes (edge_walk) ─────────────────────────────────

    private async Task<List<EdgeData>> GetEdgesAsync(
        List<(double Lat, double Lon)> coords,
        CancellationToken ct)
    {
        var request = new
        {
            encoded_polyline = PolylineEncoder.Encode(coords),
            costing          = "auto",
            shape_match      = "map_snap",
            filters          = new
            {
                attributes = new[]
                {
                    "edge.way_id",
                    "edge.length",
                    "edge.speed",
                    "matched.point",
                    "matched.edge_index",
                    "matched.type"
                },
                action = "include"
            }
        };

        var json   = JsonSerializer.Serialize(request);
        var client = httpClientFactory.CreateClient("valhalla");

        try
        {
            using var resp = await client.PostAsync(
                $"{_valhallaBaseUrl}/trace_attributes",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    var errBody = await resp.Content.ReadAsStringAsync(ct);
                    logger.LogWarning("trace_attributes {Status}: {Body}", resp.StatusCode, errBody);
                }
                return [];
            }

            var body   = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<TraceAttributesResponse>(body, _jsonOptions);
            if (parsed?.Edges == null || parsed.MatchedPoints == null) return [];

            return BuildEdgeData(coords, parsed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "trace_attributes call failed.");
            return [];
        }
    }

    private static List<EdgeData> BuildEdgeData(
        List<(double Lat, double Lon)> coords,
        TraceAttributesResponse parsed)
    {
        // Group input shape-point indices by edge index
        var pointsByEdge = new Dictionary<int, List<int>>();
        for (int i = 0; i < parsed.MatchedPoints!.Count; i++)
        {
            var mp = parsed.MatchedPoints[i];
            if (mp.EdgeIndex is null || mp.EdgeIndex == ulong.MaxValue) continue;
            var ei = (int)mp.EdgeIndex.Value;
            if (!pointsByEdge.TryGetValue(ei, out var list))
                pointsByEdge[ei] = list = [];
            list.Add(i);
        }

        var result = new List<EdgeData>();
        for (int ei = 0; ei < parsed.Edges!.Count; ei++)
        {
            var e = parsed.Edges[ei];
            if (e.WayId is null) continue;

            var pts          = new List<(double, double)>();
            var shapeIndices = new List<int>();

            if (pointsByEdge.TryGetValue(ei, out var indices))
            {
                if (result.Count > 0 && result[^1].Coords.Count > 0)
                    pts.Add(result[^1].Coords[^1]);

                foreach (var idx in indices.OrderBy(x => x))
                {
                    if (idx < coords.Count)
                    {
                        pts.Add(coords[idx]);
                        shapeIndices.Add(idx);
                    }
                }
            }

            result.Add(new EdgeData(
                WayId:        e.WayId.Value,
                LengthKm:     e.Length ?? 0,
                Coords:       pts,
                ShapeIndices: shapeIndices));
        }

        return result;
    }

    // ── Jam detection ─────────────────────────────────────────────────────────

    private static List<TrafficJam> DetectJams(
        List<(EdgeData Edge, double FreeflowSpeed, double LiveSpeed, double LengthKm, bool HasLiveData)> edges)
    {
        var jams      = new List<TrafficJam>();
        var jamCoords = new List<(double Lat, double Lon)>();
        double jamLengthKm = 0, jamFfSec = 0, jamLiveSec = 0, jamFfKmhSum = 0, jamLiveKmhSum = 0;
        int jamEdgeCount = 0;

        void FlushJam()
        {
            if (jamEdgeCount == 0) return;
            double delaySec = jamLiveSec - jamFfSec;
            if (delaySec >= MinJamDelaySeconds)
                jams.Add(new TrafficJam
                {
                    EncodedPolyline  = PolylineEncoder.Encode(jamCoords),
                    DelayMinutes     = Math.Round(delaySec / 60.0, 2),
                    LiveSpeedKmh     = Math.Round(jamLiveSec > 0 ? jamLengthKm / jamLiveSec * 3600 : 0, 1),
                    FreeflowSpeedKmh = Math.Round(jamFfSec  > 0 ? jamLengthKm / jamFfSec  * 3600 : 0, 1),
                    DelayPercent     = Math.Round(jamFfSec > 0 ? delaySec / jamFfSec * 100 : 0, 1),
                    LengthKm         = Math.Round(jamLengthKm, 3)
                });

            jamCoords.Clear();
            jamLengthKm = jamFfSec = jamLiveSec = jamFfKmhSum = jamLiveKmhSum = 0;
            jamEdgeCount = 0;
        }

        foreach (var (edge, freeflow, live, lengthKm, hasLiveData) in edges)
        {
            if (hasLiveData && live < freeflow * JamThreshold)
            {
                if (jamCoords.Count == 0) jamCoords.AddRange(edge.Coords);
                else if (edge.Coords.Count > 0) jamCoords.AddRange(edge.Coords.Skip(1));

                jamLengthKm    += lengthKm;
                jamFfSec       += lengthKm / Math.Max(1,freeflow) * 3600;
                jamLiveSec     += lengthKm / Math.Max(1,live)     * 3600;
                jamFfKmhSum    += freeflow;
                jamLiveKmhSum  += live;
                jamEdgeCount++;
            }
            else FlushJam();
        }

        FlushJam();
        return jams;
    }

    // ── Jam-avoidance detour ──────────────────────────────────────────────────

    // Only attempt a detour if a route has a jam with at least this much delay.
    private const double MinDetourJamDelayMinutes = 5.0;

    /// <summary>
    /// For the significant jams on the current best route, build Valhalla
    /// <c>exclude_polygons</c> for each jam and request a fresh route that must
    /// exit before the jam and rejoin after it.  The result is a single
    /// (TripNode, RouteAnalysisResult) candidate that is merged back into the
    /// main pool and re-ranked against the original candidates.
    /// Returns an empty list when there are no actionable jams or the request fails.
    /// </summary>
    private async Task<List<(JsonNode TripNode, RouteAnalysisResult Result)>> TryJamDetourAsync(
        JsonElement originalRequest,
        List<TrafficJam> jams,
        DateTime? departureUtc,
        CancellationToken ct)
    {
        var excludeLocations = BuildExcludeLocations(jams);
        if (excludeLocations is null) return [];

        // Single best-route request — no alternates needed for the detour.
        var detourReq = JsonNode.Parse(originalRequest.GetRawText())!.AsObject();
        detourReq["alternates"]        = 0;
        detourReq["exclude_locations"] = excludeLocations;

        var client = httpClientFactory.CreateClient("valhalla");
        var body   = new StringContent(detourReq.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await client.PostAsync($"{_valhallaBaseUrl}/route", body, ct);
            if (!response.IsSuccessStatusCode)
            {
                string data = await response.Content.ReadAsStringAsync();
                return [];
            }

            var rawJson     = await response.Content.ReadAsStringAsync(ct);
            var detourParsed = JsonSerializer.Deserialize<ValhallaResponse>(rawJson);
            if (detourParsed?.Trip?.Legs == null || detourParsed.Trip.Legs.Count == 0) return [];

            var legShapes = ExtractLegShapes(detourParsed.Trip);
            var result    = await AnalyseRouteAsync(0, legShapes, departureUtc, ct);
            var tripNode  = JsonNode.Parse(rawJson)!.AsObject()["trip"]?.DeepClone();
            if (tripNode is null) return [];

            return [(tripNode, result)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jam-avoidance detour request failed.");
            return [];
        }
    }

    /// <summary>
    /// Build a Valhalla <c>exclude_locations</c> array from significant traffic jams.
    /// Each decoded shape point of qualifying jams becomes an {"lat":…,"lon":…} entry,
    /// so only the exact congested road segments are excluded rather than a broad bbox.
    /// Returns null when no jams meet the delay threshold.
    /// </summary>
    private static JsonArray? BuildExcludeLocations(List<TrafficJam> jams)
    {
        const int MaxLocations = 50;

        var qualifying = jams
            .Where(j => j.DelayMinutes >= MinDetourJamDelayMinutes)
            .OrderByDescending(j => j.DelayMinutes)
            .Take(5)
            .ToList();

        if (qualifying.Count == 0) return null;

        // Decode all jam points upfront
        var jamPoints = qualifying
            .Select(j => PolylineEncoder.Decode(j.EncodedPolyline).ToList())
            .ToList();

        // Distribute MaxLocations slots across jams, at least 1 per jam
        int remaining = MaxLocations;
        var slots = new int[jamPoints.Count];
        for (int i = 0; i < slots.Length; i++) { slots[i] = 1; remaining--; }

        // Give extra slots proportionally by delay magnitude
        double totalDelay = qualifying.Sum(j => j.DelayMinutes);
        for (int i = 0; i < slots.Length && remaining > 0; i++)
        {
            int extra = (int)Math.Floor(qualifying[i].DelayMinutes / totalDelay * (MaxLocations - jamPoints.Count));
            extra = Math.Min(extra, remaining);
            slots[i] += extra;
            remaining -= extra;
        }
        // Assign any leftover slots to the worst jam
        slots[0] += remaining;

        var locations = new JsonArray();
        for (int i = 0; i < jamPoints.Count; i++)
        {
            var pts = jamPoints[i];
            int count = Math.Min(slots[i], pts.Count);
            if (count <= 0) continue;

            // Sample evenly if we have more points than slots
            for (int k = 0; k < count; k++)
            {
                int idx = pts.Count == 1 ? 0 : (int)Math.Round((double)k / (count - 1) * (pts.Count - 1));
                locations.Add(new JsonObject { ["lat"] = pts[idx].Lat, ["lon"] = pts[idx].Lon });
            }
        }

        return locations.Count > 0 ? locations : null;
    }

    // ── Composite ranking ──────────────────────────────────────────────────────

    /// <summary>
    /// Rank routes by a composite score: 70% travel time, 30% eco (CO2).
    /// Both dimensions are normalised to [0..1] within the candidate pool
    /// so that the weighting is meaningful regardless of absolute values.
    /// </summary>
    private static List<(Trip Trip, RouteAnalysisResult Result)> RankByComposite(
        List<(Trip Trip, RouteAnalysisResult Result)> candidates)
    {
        if (candidates.Count <= 1) return candidates;

        double minTime = candidates.Min(c => c.Result.Analysis.LiveDurationMinutes);
        double maxTime = candidates.Max(c => c.Result.Analysis.LiveDurationMinutes);
        double minCo2  = candidates.Min(c => c.Result.Analysis.Co2GramsEstimate);
        double maxCo2  = candidates.Max(c => c.Result.Analysis.Co2GramsEstimate);

        double timeRange = maxTime - minTime;
        double co2Range  = maxCo2  - minCo2;

        return candidates
            .OrderBy(c =>
            {
                // Time: lower is better → normalise so 0 = fastest
                double normTime = timeRange > 0 ? (c.Result.Analysis.LiveDurationMinutes - minTime) / timeRange : 0;
                // Eco: higher is better → invert so 0 = greenest
                double normEco  = co2Range  > 0 ? (maxCo2 - c.Result.Analysis.Co2GramsEstimate) / co2Range : 0;
                return 0.70 * normTime + 0.30 * (1.0 - normEco);
            })
            .ToList();
    }

    // ── Request manipulation ──────────────────────────────────────────────────

    /// <summary>
    /// Extract the departure time from the Valhalla date_time object.
    /// Returns null if not present or not parseable (meaning "now").
    /// Supports type 1 (depart_at) with value "YYYY-MM-DDTHH:MM".
    /// </summary>
    private static DateTime? ExtractDepartureTime(JsonElement source)
    {
        if (!source.TryGetProperty("date_time", out var dt)) return null;
        if (!dt.TryGetProperty("type", out var typeProp)) return null;
        int type = typeProp.GetInt32();
        // type 0 = current time, type 1 = depart_at, type 2 = arrive_by
        if (type == 0) return null;
        if (!dt.TryGetProperty("value", out var valueProp)) return null;
        var valueStr = valueProp.GetString();
        if (string.IsNullOrEmpty(valueStr)) return null;
        return DateTime.TryParse(valueStr, out var parsed) ? parsed : null;
    }

    private static int ReadAlternates(JsonElement source)
    {
        if (source.TryGetProperty("alternates", out var prop) &&
            prop.TryGetInt32(out var n) && n > 0)
            return n;
        return 0;
    }

    private static JsonObject InjectAlternates(JsonElement source, int alternates)
    {
        var node = JsonNode.Parse(source.GetRawText())!.AsObject();
        node["alternates"] = alternates;
        return node;
    }

    /// <summary>
    /// Apply server-side defaults for costing options, shortcuts, etc.
    /// Only sets values that are not already present in the request.
    /// </summary>
    private static JsonObject ApplyDefaults(JsonElement source)
    {
        var node = JsonNode.Parse(source.GetRawText())!.AsObject();

        node.TryAdd("shortcuts", false);

        // Determine the costing model (default: "auto")
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

    // ── Response rebuilding ───────────────────────────────────────────────────

    /// <summary>
    /// Rebuild the Valhalla response from a ranked list of (trip node, analysis) pairs.
    /// Accepts trip nodes from any Valhalla response (original candidates or jam detours).
    /// Each trip's time fields are patched with traffic delays; trafficAnalysis is injected.
    /// Root-level fields (id, etc.) are copied verbatim from <paramref name="original"/>.
    /// </summary>
    private static JsonElement RebuildValhallaResponse(
        JsonElement original,
        List<(JsonNode TripNode, RouteAnalysisResult Result)> rankedTrips)
    {
        if (rankedTrips.Count == 0) return original;

        var root    = JsonNode.Parse(original.GetRawText())!.AsObject();
        var newRoot = new JsonObject();

        foreach (var prop in root)
        {
            if (prop.Key is "trip" or "alternates") continue;
            newRoot[prop.Key] = prop.Value?.DeepClone();
        }

        static JsonNode PatchAndInject(JsonNode tripNode, RouteAnalysisResult result)
        {
            var obj = tripNode.AsObject();
            PatchTripTimes(obj, result.LegShapeDelays);
            obj["trafficAnalysis"] = JsonNode.Parse(
                JsonSerializer.Serialize(result.Analysis, _camelCaseOptions));
            return obj;
        }

        newRoot["trip"] = PatchAndInject(rankedTrips[0].TripNode, rankedTrips[0].Result);

        var newAlts = new JsonArray();
        for (int i = 1; i < rankedTrips.Count; i++)
            newAlts.Add(new JsonObject { ["trip"] = PatchAndInject(rankedTrips[i].TripNode, rankedTrips[i].Result) });

        if (newAlts.Count > 0) newRoot["alternates"] = newAlts;

        return JsonDocument.Parse(newRoot.ToJsonString()).RootElement;
    }

    /// <summary>
    /// Patch all time fields inside a trip JSON object using per-leg shape-point delay arrays.
    /// <list type="bullet">
    ///   <item>maneuver.time += sum of shapePointDelays[begin_shape_index..end_shape_index]</item>
    ///   <item>leg.summary.time = sum of patched maneuver times</item>
    ///   <item>trip.summary.time = sum of patched leg summary times</item>
    /// </list>
    /// </summary>
    private static void PatchTripTimes(JsonObject trip, List<double[]> legShapeDelays)
    {
        if (!trip.TryGetPropertyValue("legs", out var legsNode) || legsNode is not JsonArray legs)
            return;

        double tripTime = 0;

        for (int li = 0; li < legs.Count; li++)
        {
            if (legs[li] is not JsonObject leg) continue;

            var delays  = li < legShapeDelays.Count ? legShapeDelays[li] : null;
            double legTime = 0;

            if (leg.TryGetPropertyValue("maneuvers", out var manNode) && manNode is JsonArray maneuvers)
            {
                foreach (var manItem in maneuvers)
                {
                    if (manItem is not JsonObject man) continue;

                    double original  = man["time"]?.GetValue<double>() ?? 0;
                    double addedDelay = 0;

                    if (delays is not null &&
                        man.TryGetPropertyValue("begin_shape_index", out var bNode) &&
                        man.TryGetPropertyValue("end_shape_index",   out var eNode))
                    {
                        int begin = bNode?.GetValue<int>() ?? 0;
                        int end   = eNode?.GetValue<int>() ?? 0;
                        for (int idx = begin; idx <= end && (uint)idx < (uint)delays.Length; idx++)
                            addedDelay += delays[idx];
                    }

                    double newTime = original + addedDelay;
                    man["time"] = Math.Round(newTime, 3);
                    legTime += newTime;
                }
            }

            if (leg.TryGetPropertyValue("summary", out var sumNode) && sumNode is JsonObject legSummary)
                legSummary["time"] = Math.Round(legTime, 3);

            tripTime += legTime;
        }

        if (trip.TryGetPropertyValue("summary", out var tripSumNode) && tripSumNode is JsonObject tripSummary)
            tripSummary["time"] = Math.Round(tripTime, 3);
    }

    // ── Internal types ────────────────────────────────────────────────────────

    private record RouteAnalysisResult(
        RouteTrafficAnalysis Analysis,
        List<double[]> LegShapeDelays);

    private record EdgeData(
        long WayId,
        double LengthKm,
        List<(double Lat, double Lon)> Coords,
        List<int> ShapeIndices);

    // ── Valhalla response DTOs ────────────────────────────────────────────────

    private class TraceAttributesResponse
    {
        [JsonPropertyName("edges")]
        public List<TraceEdge>? Edges { get; set; }

        [JsonPropertyName("matched_points")]
        public List<TraceMatchedPoint>? MatchedPoints { get; set; }
    }

    private class TraceEdge
    {
        [JsonPropertyName("way_id")]
        public long? WayId { get; set; }

        [JsonPropertyName("length")]
        public double? Length { get; set; }

        [JsonPropertyName("speed")]
        public double? Speed { get; set; }
    }

    private class TraceMatchedPoint
    {
        [JsonPropertyName("edge_index")]
        public ulong? EdgeIndex { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }
}
