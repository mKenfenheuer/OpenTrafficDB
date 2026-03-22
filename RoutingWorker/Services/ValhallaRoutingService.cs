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

        // ── Two parallel Valhalla requests ──────────────────────────────────
        // 1) Main request: no date_time → supports alternates
        var mainRequest = InjectAlternates(defaultsElement, userAlternates);

        // 2) Time-dependent request: date_time={type:0}, no alternates
        //    Gets Valhalla's own time-aware costing for the optimal route.
        var tdRequest = JsonNode.Parse(withDefaults.ToJsonString())!.AsObject();
        tdRequest["alternates"] = 0;
        tdRequest["date_time"]  = new JsonObject { ["type"] = 0 };

        var client = httpClientFactory.CreateClient("valhalla");

        var mainTask = client.PostAsync($"{_valhallaBaseUrl}/route",
            new StringContent(mainRequest.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var tdTask   = client.PostAsync($"{_valhallaBaseUrl}/route",
            new StringContent(tdRequest.ToJsonString(), Encoding.UTF8, "application/json"), ct);

        HttpResponseMessage mainResponse;
        try
        {
            mainResponse = await mainTask;
        }
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

        JsonElement valhallaDoc;
        try { valhallaDoc = JsonDocument.Parse(rawJson).RootElement; }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Valhalla /route response.");
            return (null, 502, "Invalid JSON from Valhalla.");
        }

        var routeLegShapes = ExtractRouteLegShapes(valhallaDoc);
        var tripNodes      = ExtractAllTripNodes(valhallaDoc);

        // Merge the time-dependent route into the candidate pool
        try
        {
            var tdResponse = await tdTask;
            if (tdResponse.IsSuccessStatusCode)
            {
                var tdJson = await tdResponse.Content.ReadAsStringAsync(ct);
                var tdDoc  = JsonDocument.Parse(tdJson).RootElement;
                var tdLegShapes = ExtractRouteLegShapes(tdDoc);
                var tdTripNodes = ExtractAllTripNodes(tdDoc);
                routeLegShapes.AddRange(tdLegShapes);
                tripNodes.AddRange(tdTripNodes);
            }
            else if (logger.IsEnabled(LogLevel.Debug))
            {
                var errBody = await tdResponse.Content.ReadAsStringAsync(ct);
                logger.LogDebug("Time-dependent /route returned {Status}: {Body}", tdResponse.StatusCode, errBody);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Time-dependent /route request failed (non-fatal).");
        }

        // Analyse traffic for every candidate in parallel
        var results = new RouteAnalysisResult[routeLegShapes.Count];
        using var sem = new SemaphoreSlim(4);
        await Task.WhenAll(routeLegShapes.Select(async (legShapes, i) =>
        {
            await sem.WaitAsync(ct);
            try   { results[i] = await AnalyseRouteAsync(i, legShapes, ct); }
            finally { sem.Release(); }
        }));

        // Build unified candidate pool, deduplicate by shape, rank by eco score
        var seen = new HashSet<string>();
        var pool = results
            .Select((r, i) => (TripNode: tripNodes[i], Result: r))
            .Where(x => x.TripNode is not null)
            .Select(x => (TripNode: x.TripNode!, Result: x.Result))
            .OrderBy(x => x.Result.Analysis.EcoScore)
            .Where(x => seen.Add(GetRouteShapeKey(x.TripNode)))
            .ToList();

        // Trim to user-requested count
        var finalRanked = pool.Take(requestedRoutes).ToList();

        // Try a jam-avoiding detour for every route and append results
        var detourTasks = finalRanked
            .Select(r => TryJamDetourAsync(clientRequest, r.Result.Analysis.Jams, ct));
        var allDetours = await Task.WhenAll(detourTasks);

        // Merge detours, deduplicate again, trim back — rank by eco score
        finalRanked.AddRange(allDetours.SelectMany(d => d));
        finalRanked = finalRanked
            .OrderBy(x => x.Result.Analysis.EcoScore)
            .Where(x => seen.Add(GetRouteShapeKey(x.TripNode)))
            .Take(requestedRoutes)
            .ToList();

        for (int i = 0; i < finalRanked.Count; i++)
            finalRanked[i].Result.Analysis.RouteIndex = i;

        var rebuilt = RebuildValhallaResponse(valhallaDoc, finalRanked);
        return (rebuilt, 200, null);
    }

    // ── Shape / node extraction ───────────────────────────────────────────────

    /// <summary>
    /// Extract the raw trip JsonNode for every route in the Valhalla response.
    /// Index 0 = primary trip; 1..N = alternates (each wrapped in {trip:…}).
    /// Returns null slots for indices that cannot be resolved.
    /// </summary>
    private static List<JsonNode?> ExtractAllTripNodes(JsonElement doc)
    {
        var root  = JsonNode.Parse(doc.GetRawText())!.AsObject();
        var nodes = new List<JsonNode?> { root["trip"]?.DeepClone() };

        if (root["alternates"] is JsonArray alts)
            foreach (var alt in alts)
                nodes.Add(alt?["trip"]?.DeepClone());

        return nodes;
    }

    /// <summary>
    /// Returns [routeIndex][legIndex] → decoded shape coords.
    /// Legs are NOT concatenated — each leg keeps its own shape-index space.
    /// </summary>
    private static List<List<List<(double Lat, double Lon)>>> ExtractRouteLegShapes(JsonElement root)
    {
        var result = new List<List<List<(double, double)>>>();

        if (root.TryGetProperty("trip", out var primaryTrip))
            result.Add(DecodeLegShapes(primaryTrip));

        if (root.TryGetProperty("alternates", out var alts) && alts.ValueKind == JsonValueKind.Array)
            foreach (var alt in alts.EnumerateArray())
                if (alt.TryGetProperty("trip", out var altTrip))
                    result.Add(DecodeLegShapes(altTrip));

        return result;
    }

    private static List<List<(double Lat, double Lon)>> DecodeLegShapes(JsonElement trip)
    {
        var legs = new List<List<(double, double)>>();
        if (!trip.TryGetProperty("legs", out var legsEl) || legsEl.ValueKind != JsonValueKind.Array)
            return legs;

        foreach (var leg in legsEl.EnumerateArray())
        {
            if (!leg.TryGetProperty("shape", out var shapeProp)) continue;
            var shape = shapeProp.GetString();
            if (string.IsNullOrEmpty(shape)) continue;
            legs.Add([.. PolylineEncoder.Decode(shape)]);
        }

        return legs;
    }

    // ── Per-route traffic analysis ────────────────────────────────────────────

    private async Task<RouteAnalysisResult> AnalyseRouteAsync(
        int routeIndex,
        List<List<(double Lat, double Lon)>> legShapes,
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

        // Current time-of-day bucket for historical fallback
        var now       = DateTime.UtcNow;
        int currentDow    = (int)now.DayOfWeek;
        int currentBucket = (now.Hour * 60 + now.Minute) / 5;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var summaries = await db.WaySpeedSummaries
            .AsNoTracking()
            .Where(s => wayIds.Contains(s.OsmWayId))
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        // Historical speed for the current day-of-week + 5-minute bucket
        var historicalSpeeds = await db.WaySpeedAggregates
            .AsNoTracking()
            .Where(a => wayIds.Contains(a.OsmWayId)
                     && a.DayOfWeek == currentDow
                     && a.FiveMinuteBucket == currentBucket
                     && a.SampleCount > 0)
            .ToDictionaryAsync(a => a.OsmWayId, a => a.SumSpeedKmh / a.SampleCount, ct);

        double totalFreeflowSec = 0, totalLiveSec = 0, totalKm = 0, coveredKm = 0;
        var allEdgeInfo = new List<(EdgeData Edge, double FreeflowSpeed, double LiveSpeed, double LengthKm, bool HasLiveData)>();
        var legShapeDelays = new List<double[]>(legShapes.Count);

        for (int li = 0; li < legEdges.Count; li++)
        {
            var (legDelay, edgeInfo, ffSec, livSec, km, covKm) =
                ComputeLegDelays(legEdges[li], legShapes[li].Count, summaries, historicalSpeeds);

            legShapeDelays.Add(legDelay);
            allEdgeInfo.AddRange(edgeInfo);
            totalFreeflowSec += ffSec;
            totalLiveSec     += livSec;
            totalKm          += km;
            coveredKm        += covKm;
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

        // Eco score: CO2 as primary factor, with a distance-per-minute multiplier
        // that penalises routes covering more distance per unit time (i.e. longer
        // routes that save little time). The multiplier is gentle — it nudges
        // ranking without overwhelming the CO2 signal.
        double ecoScore = totalCo2Grams * (1.0 + 0.05 * distPerMin);

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
    private static (
        double[] ShapePointDelays,
        List<(EdgeData, double, double, double, bool)> EdgeInfo,
        double FreeflowSec, double LiveSec, double TotalKm, double CoveredKm)
        ComputeLegDelays(
            List<EdgeData> edges,
            int shapePointCount,
            Dictionary<long, WaySpeedSummary> summaries,
            Dictionary<long, double> historicalSpeeds)
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
                // Prefer live EMA, then time-of-day historical, then freeflow baseline
                if (s.ConstrainedSpeed.HasValue)
                {
                    live    = Math.Max(s.ConstrainedSpeed.Value, 1);
                    hasData = true;
                }
                else if (historicalSpeeds.TryGetValue(edge.WayId, out var hist) && hist > 0)
                {
                    live    = hist;
                    hasData = true;
                }
                else
                {
                    live    = freeflow;
                    hasData = false;
                }
            }

            if (freeflow <= 0) freeflow = 50;
            if (live     <= 0) live     = freeflow;

            double edgeFreeflowSec = edge.LengthKm / freeflow * 3600;
            double edgeLiveSec     = edge.LengthKm / live     * 3600;
            double edgeDelaySec    = edgeLiveSec - edgeFreeflowSec;

            freeflowSec += edgeFreeflowSec;
            liveSec     += edgeLiveSec;
            totalKm     += edge.LengthKm;
            if (hasData) coveredKm += edge.LengthKm;

            edgeInfo.Add((edge, freeflow, live, edge.LengthKm, hasData));

            // Distribute delay across this edge's matched shape points
            if (edge.ShapeIndices.Count > 0 && edgeDelaySec > 0)
            {
                double delayPerPoint = edgeDelaySec / edge.ShapeIndices.Count;
                foreach (var idx in edge.ShapeIndices)
                    if ((uint)idx < (uint)shapePointDelays.Length)
                        shapePointDelays[idx] += delayPerPoint;
            }
        }

        return (shapePointDelays, edgeInfo, freeflowSec, liveSec, totalKm, coveredKm);
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

    // Only attempt a detour if the best route has a jam with at least this much delay.
    private const double MinDetourJamDelayMinutes = 2.0;

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

            var rawJson    = await response.Content.ReadAsStringAsync(ct);
            var detourDoc  = JsonDocument.Parse(rawJson).RootElement;
            var legShapes  = ExtractRouteLegShapes(detourDoc);
            if (legShapes.Count == 0) return [];

            var result   = await AnalyseRouteAsync(0, legShapes[0], ct);
            var tripNode = JsonNode.Parse(rawJson)!.AsObject()["trip"]?.DeepClone();
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

    // ── Request manipulation ──────────────────────────────────────────────────

    /// <summary>
    /// Produces a deduplication key by concatenating the encoded shape strings of all
    /// legs in a trip node. Two routes with identical shapes are considered duplicates.
    /// </summary>
    private static string GetRouteShapeKey(JsonNode tripNode)
    {
        if (tripNode["legs"] is not JsonArray legs) return string.Empty;

        var sb = new StringBuilder();
        foreach (var leg in legs)
            sb.Append(leg?["shape"]?.GetValue<string>());
        return sb.ToString();
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
