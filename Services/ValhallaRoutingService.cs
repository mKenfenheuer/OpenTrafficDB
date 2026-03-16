using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

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
/// </summary>
public class ValhallaRoutingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    AppDbContext db,
    ILogger<ValhallaRoutingService> logger)
{
    private readonly string _valhallaBaseUrl = config["Valhalla:BaseUrl"] ?? "http://localhost:8002";

    private const double JamThreshold      = 0.75;
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

    public async Task<(JsonElement? Result, int ValhallaStatus, string? ValhallaError)>
        RouteWithTrafficAsync(JsonElement clientRequest, CancellationToken ct)
    {
        int userAlternates  = ReadAlternates(clientRequest);
        int requestedRoutes = userAlternates + 1;

        var routeRequest = InjectAlternates(clientRequest, Math.Max(userAlternates, 10));

        var client = httpClientFactory.CreateClient("valhalla");
        var body   = new StringContent(routeRequest.ToJsonString(), Encoding.UTF8, "application/json");

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

        JsonElement valhallaDoc;
        try { valhallaDoc = JsonDocument.Parse(rawJson).RootElement; }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Valhalla /route response.");
            return (null, 502, "Invalid JSON from Valhalla.");
        }

        var routeLegShapes = ExtractRouteLegShapes(valhallaDoc);

        var results = new RouteAnalysisResult[routeLegShapes.Count];
        using var sem = new SemaphoreSlim(4);
        await Task.WhenAll(routeLegShapes.Select(async (legShapes, i) =>
        {
            await sem.WaitAsync(ct);
            try   { results[i] = await AnalyseRouteAsync(i, legShapes, ct); }
            finally { sem.Release(); }
        }));

        var ranked = results
            .Select((r, originalIndex) => (Result: r, OriginalIndex: originalIndex))
            .OrderBy(x => x.Result.Analysis.LiveDurationMinutes)
            .Take(requestedRoutes)
            .ToList();

        var rebuilt = RebuildValhallaResponse(
            valhallaDoc,
            [.. ranked.Select(x => x.OriginalIndex)],
            [.. ranked.Select(x => x.Result)]);

        return (rebuilt, 200, null);
    }

    // ── Shape extraction ──────────────────────────────────────────────────────

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

        // Single DB query across all legs
        var wayIds = legEdges
            .SelectMany(edges => edges.Select(e => e.WayId))
            .Where(id => id != 0).Distinct().ToList();

        var summaries = await db.WaySpeedSummaries
            .AsNoTracking()
            .Where(s => wayIds.Contains(s.OsmWayId))
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        double totalFreeflowSec = 0, totalLiveSec = 0, totalKm = 0, coveredKm = 0;
        var allEdgeInfo = new List<(EdgeData Edge, double FreeflowSpeed, double LiveSpeed, double LengthKm, bool HasLiveData)>();
        var legShapeDelays = new List<double[]>(legShapes.Count);

        for (int li = 0; li < legEdges.Count; li++)
        {
            var (legDelay, edgeInfo, ffSec, livSec, km, covKm) =
                ComputeLegDelays(legEdges[li], legShapes[li].Count, summaries);

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

        var analysis = new RouteTrafficAnalysis
        {
            RouteIndex              = routeIndex,
            FreeflowDurationMinutes = Math.Round(ffMin,                              2),
            LiveDurationMinutes     = Math.Round(liveMin,                            2),
            TotalDelayMinutes       = Math.Round(delayMin,                           2),
            DelayPercent            = Math.Round(ffMin > 0 ? delayMin / ffMin * 100 : 0, 1),
            TrafficCoverageRatio    = Math.Round(totalKm > 0 ? coveredKm / totalKm  : 0, 3),
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
                freeflow = s.FreeflowSpeed > 0 ? s.FreeflowSpeed : s.P50Speed;
                live     = s.ConstrainedSpeed ?? freeflow;
                hasData  = s.ConstrainedSpeed.HasValue;
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
            shape_match      = "edge_walk",
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
                    LiveSpeedKmh     = Math.Round(jamLiveKmhSum / jamEdgeCount, 1),
                    FreeflowSpeedKmh = Math.Round(jamFfKmhSum   / jamEdgeCount, 1),
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
                jamFfSec       += lengthKm / freeflow * 3600;
                jamLiveSec     += lengthKm / live     * 3600;
                jamFfKmhSum    += freeflow;
                jamLiveKmhSum  += live;
                jamEdgeCount++;
            }
            else FlushJam();
        }

        FlushJam();
        return jams;
    }

    // ── Request manipulation ──────────────────────────────────────────────────

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

    // ── Response rebuilding ───────────────────────────────────────────────────

    /// <summary>
    /// Rebuild the Valhalla response with only the traffic-ranked routes.
    /// Each trip's time fields are patched with traffic delays; trafficAnalysis is injected.
    /// Root-level fields (id, etc.) are preserved verbatim.
    /// </summary>
    private static JsonElement RebuildValhallaResponse(
        JsonElement original,
        List<int> orderedOriginalIndices,
        List<RouteAnalysisResult> results)
    {
        if (orderedOriginalIndices.Count == 0) return original;

        var root = JsonNode.Parse(original.GetRawText())!.AsObject();

        JsonNode? GetTripNode(int idx)
        {
            if (idx == 0) return root["trip"]?.DeepClone();
            if (root["alternates"] is JsonArray alts && idx - 1 < alts.Count)
                return alts[idx - 1]?["trip"]?.DeepClone();
            return null;
        }

        var newRoot = new JsonObject();
        foreach (var prop in root)
        {
            if (prop.Key is "trip" or "alternates") continue;
            newRoot[prop.Key] = prop.Value?.DeepClone();
        }

        JsonNode PatchAndInject(JsonNode tripNode, RouteAnalysisResult result)
        {
            var obj = tripNode.AsObject();
            PatchTripTimes(obj, result.LegShapeDelays);
            obj["trafficAnalysis"] = JsonNode.Parse(
                JsonSerializer.Serialize(result.Analysis, _camelCaseOptions));
            return obj;
        }

        var bestTrip = GetTripNode(orderedOriginalIndices[0]);
        if (bestTrip is not null)
            newRoot["trip"] = PatchAndInject(bestTrip, results[0]);

        var newAlts = new JsonArray();
        for (int i = 1; i < orderedOriginalIndices.Count; i++)
        {
            var tripNode = GetTripNode(orderedOriginalIndices[i]);
            if (tripNode is null) continue;
            newAlts.Add(new JsonObject { ["trip"] = PatchAndInject(tripNode, results[i]) });
        }

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
