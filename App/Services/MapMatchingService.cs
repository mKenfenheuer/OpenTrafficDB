using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Performs server-side map matching of GPS observations to OSM road segments
/// using the OpenTrafficRouting /api/v1/trace/bulk endpoint.
///
/// The bulk endpoint accepts multiple encoded polylines in a single request,
/// which is more efficient than individual trace calls per trip segment.
/// </summary>
public class MapMatchingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ReadOnlyAppDbContext readOnlyAppDb,
    ILogger<MapMatchingService> logger)
{
    private readonly string _otrBaseUrl = config["OpenTrafficRouting:BaseUrl"] ?? "http://localhost:8002";
    private readonly int _maxConcurrent = int.TryParse(config["OpenTrafficRouting:MaxConcurrent"], out var mc) ? mc : 4;
    private readonly int _batchSize = int.TryParse(config["OpenTrafficRouting:BatchSize"], out var bs) ? bs : 25;

    // Unmatched observations are retried only together with new points of the same trip.
    // They are given up after MaxMatchAttempts attempts or once they are older than the retry window.
    private readonly int _maxMatchAttempts = int.TryParse(config["Aggregation:MaxMatchAttempts"], out var ma) ? ma : 3;
    private readonly TimeSpan _matchRetryWindow = TimeSpan.FromSeconds(
        double.TryParse(config["Aggregation:MatchRetryWindowSeconds"], out var rw) ? rw : 300);

    private static readonly Action<ILogger, int, Exception?> _logMapMatched =
        LoggerMessage.Define<int>(LogLevel.Information, default, "Map-matched {Count} observations to OSM ways");

    private static readonly Action<ILogger, int, int, Exception?> _logProcessing =
        LoggerMessage.Define<int, int>(LogLevel.Debug, default, "Map-matching {ObsCount} observations across {TripCount} trips");

    private static readonly Action<ILogger, int, int, Exception?> _logBulkRequest =
        LoggerMessage.Define<int, int>(LogLevel.Debug, default, "Sending bulk trace request: {PolylineCount} polylines from {TripCount} trips");

    private static readonly Action<ILogger, int, int, string, Exception?> _logOtrError =
        LoggerMessage.Define<int, int, string>(LogLevel.Warning, default, "OTR bulk trace returned {StatusCode} for {PolylineCount} polylines: {Body}");

    private static readonly Action<ILogger, int, int, int, int, int, Exception?> _logParseSkip =
        LoggerMessage.Define<int, int, int, int, int>(LogLevel.Debug, default,
            "Trace parse: {Total} points → {Matched} matched, {BadType} bad type, {NoEdge} no edge index, {Excluded} excluded edge");
    private static readonly Action<ILogger, int, Exception?> _logInterpolated =
        LoggerMessage.Define<int>(LogLevel.Debug, default,
            "Map-matching returned {Interpoladed} edges.");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Result of matching a single GPS point to an OSM road segment.</summary>
    public record MatchResult(
        long OsmWayId,
        double? SpeedLimitKmh);

    private readonly record struct SynthObs(
        int ApiKeyId, string TripId, DateTime Timestamp,
        double Lat, double Lon, double SpeedMs, float Accuracy,
        long WayId, double? SpeedLimit);

    /// <summary>
    /// Match multiple GPS traces to OSM ways via the OpenTrafficRouting /api/v1/trace/bulk endpoint.
    /// Returns raw TraceResult objects (one per polyline) so callers can access both point matches
    /// and interpolated edges.
    /// </summary>
    public async Task<List<TraceResult?>> MatchTracesBulkAsync(
        IReadOnlyList<string> encodedPolylines,
        CancellationToken ct = default)
    {
        if (encodedPolylines.Count == 0) return [];

        var request = new
        {
            polylines = encodedPolylines,
            costing = "auto",
            gps_accuracy = 10.0,
            search_radius = 50.0,
            interpolate = true,
            attributes = new[] { "osm_way_id" },
            filters = new { road_types = _allowedRoadTypes.ToArray() }
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var client = httpClientFactory.CreateClient("otr");

        try
        {
            using var response = await client.PostAsync(
                $"{_otrBaseUrl}/api/v1/trace/bulk",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logOtrError(logger, (int)response.StatusCode, encodedPolylines.Count, errorBody, null);
                return Enumerable.Repeat<TraceResult?>(null, encodedPolylines.Count).ToList();
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var wrapper = JsonSerializer.Deserialize<BulkTraceResponse>(body, _jsonOptions);
            var results = wrapper?.Results;

            if (results == null)
                return Enumerable.Repeat<TraceResult?>(null, encodedPolylines.Count).ToList();

            return results.Cast<TraceResult?>().ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenTrafficRouting bulk trace failed.");
            return Enumerable.Repeat<TraceResult?>(null, encodedPolylines.Count).ToList();
        }
    }

    /// <summary>
    /// Match a single GPS trace. Delegates to the bulk endpoint with one polyline.
    /// Returns a dictionary mapping observation index to MatchResult (point matches only).
    /// </summary>
    public async Task<Dictionary<int, MatchResult>> MatchTraceAsync(
        IReadOnlyList<(double Lat, double Lon, DateTime Time)> data,
        CancellationToken ct = default)
    {
        var points = data.ToList();
        if (points.Count == 0) return [];
        if (points.Count == 1)
            points = [points[0], points[0]];

        var polyline = EncodePolyline6(points);
        var results = await MatchTracesBulkAsync([polyline], ct);
        return results.Count > 0 ? ParsePointMatches(results[0]) : [];
    }

    /// <summary>
    /// Match observations across multiple trips using the bulk trace endpoint.
    /// Handles both direct point matches (UPDATE existing observations) and interpolated
    /// edges (INSERT synthetic observations with the same ApiKeyId and TripId).
    /// Returns the number of observations matched.
    /// </summary>
    public async Task<MatchJobResult> ProcessRangeAsync(
        List<SpeedObservation> unmatched, AppDbContext db, CancellationToken ct = default)
    {
        if (unmatched.Count == 0) return new MatchJobResult(0, [], []);

        var staleCutoff = DateTime.UtcNow - _matchRetryWindow;
        var trips = unmatched.GroupBy(o => o.TripId).ToList();
        _logProcessing(logger, unmatched.Count, trips.Count, null);

        // Split each trip into segments in parallel, then flatten.
        var perTripSegments = new List<List<SpeedObservation>>[trips.Count];
        Parallel.For(0, trips.Count, i =>
        {
            // Id breaks timestamp ties so points keep their submission order.
            var sorted = trips[i].OrderBy(o => o.Timestamp).ThenBy(o => o.Id).ToList();
            perTripSegments[i] = SplitTrace(sorted);
        });

        var allSegments = new List<(int TripIndex, int SegmentIndex, List<SpeedObservation> Segment)>();
        for (int i = 0; i < trips.Count; i++)
        {
            var segments = perTripSegments[i];
            for (int si = 0; si < segments.Count; si++)
                allSegments.Add((i, si, segments[si]));
        }

        // Encode polylines in parallel — this is pure CPU and trivially parallel.
        var allPolylines = new string[allSegments.Count];
        Parallel.For(0, allSegments.Count, i =>
        {
            var segment = allSegments[i].Segment;
            var points = new List<(double, double, DateTime)>(segment.Count);
            foreach (var o in segment)
                points.Add((o.Latitude, o.Longitude, o.Timestamp));
            if (points.Count == 1)
                points.Add(points[0]);
            allPolylines[i] = EncodePolyline6(points);
        });

        // Sort segments by geographic proximity (first point) so each batch covers
        // a small area and the map-matching service doesn't thrash tiles.
        var sortedIndices = Enumerable.Range(0, allSegments.Count)
            .OrderBy(idx =>
            {
                var seg = allSegments[idx].Segment;
                var first = seg[0];
                // Interleave lat/lon bits for a simple spatial sort (poor-man's geohash).
                // Quantize to ~1 km grid cells, then interleave for locality.
                int latQ = (int)((first.Latitude + 90.0) * 100);
                int lonQ = (int)((first.Longitude + 180.0) * 100);
                long key = 0;
                for (int b = 15; b >= 0; b--)
                {
                    key = (key << 1) | (uint)((latQ >> b) & 1);
                    key = (key << 1) | (uint)((lonQ >> b) & 1);
                }
                return key;
            })
            .ToArray();

        // Build batches of polylines in proximity order.
        var batchSize = _batchSize;
        _logBulkRequest(logger, allPolylines.Length, trips.Count, null);

        var batches = new List<List<(int OrigIndex, string Polyline)>>();
        for (int offset = 0; offset < sortedIndices.Length; offset += batchSize)
        {
            int count = Math.Min(batchSize, sortedIndices.Length - offset);
            var batch = new List<(int, string)>(count);
            for (int j = 0; j < count; j++)
            {
                int orig = sortedIndices[offset + j];
                batch.Add((orig, allPolylines[orig]));
            }
            batches.Add(batch);
        }

        var rawResults = new TraceResult?[allPolylines.Length];
        var sem = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);

        var stopwatch1 = new Stopwatch();
        stopwatch1.Start();
        await Task.WhenAll(batches.Select(async (batch, bi) =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                var results = await MatchTracesBulkAsync(batch.Select(b => b.Polyline).ToList(), ct);
                for (int i = 0; i < results.Count; i++)
                    rawResults[batch[i].OrigIndex] = results[i];
                stopwatch.Stop();
                logger.LogDebug($"Batch completed: {batch.Count} polylines in {stopwatch.ElapsedMilliseconds}ms => {stopwatch.ElapsedMilliseconds / batch.Count}ms per polyline");
            }
            finally { sem.Release(); }
        }));
        stopwatch1.Stop();

        logger.LogInformation($"Batch completed: {batches.Sum(b => b.Count)} polylines in {stopwatch1.ElapsedMilliseconds}ms => {stopwatch1.ElapsedMilliseconds / batches.Sum(b => b.Count)}ms per polyline");

        // ── Parse all segments in parallel into per-segment buffers, then concat ──
        var perSegmentMatched = new List<(long Id, long WayId, double? Limit)>[allSegments.Count];
        var perSegmentSynth = new List<SynthObs>[allSegments.Count];
        var tripMatchedIds = new HashSet<long>[trips.Count];
        for (int i = 0; i < trips.Count; i++)
            tripMatchedIds[i] = [];

        Parallel.For(0, allSegments.Count, bi =>
        {
            var (tripIdx, _, segment) = allSegments[bi];
            var result = bi < rawResults.Length ? rawResults[bi] : null;

            // Point matches
            var matchMap = ParsePointMatches(result);
            var matched = new List<(long, long, double?)>(matchMap.Count);



            foreach (var (idx, match) in matchMap)
            {
                if (idx >= segment.Count) continue;
                var obs = segment[idx];
                matched.Add((obs.Id, match.OsmWayId, match.SpeedLimitKmh));
            }

            perSegmentMatched[bi] = matched;

            // Interpolated edges
            var synth = new List<SynthObs>();
            if (result?.Edges != null && result.MatchedPoints != null)
            {
                var matchedPoints = result.MatchedPoints;
                var tripId = trips[tripIdx].Key;

                foreach (var edge in result.Edges)
                {
                    if (!edge.IsInterpolated
                        || edge.Edge == null
                        || edge.InterpolationReferences == null
                        || edge.Edge.OsmWayId == 0
                        || IsExcludedRoadType(edge.Edge.RoadType)) continue;

                    double speedMs = 0, lat = 0, lon = 0, accuracy = 0;
                    long ticks = 0;
                    int apiKeyId = 0;
                    bool hasRef = false;

                    foreach (var iref in edge.InterpolationReferences)
                    {
                        if (iref.PointIndex >= matchedPoints.Count) continue;
                        var mp = matchedPoints[iref.PointIndex];
                        if (mp.OriginalIndex >= segment.Count) continue;
                        var obs = segment[mp.OriginalIndex];

                        speedMs += iref.Percentage * obs.SpeedMs;
                        lat += iref.Percentage * obs.Latitude;
                        lon += iref.Percentage * obs.Longitude;
                        ticks += (long)(iref.Percentage * obs.Timestamp.Ticks);
                        accuracy += iref.Percentage * obs.GpsAccuracyMeters;
                        if (!hasRef) { apiKeyId = obs.ApiKeyId; hasRef = true; }
                    }

                    if (!hasRef) continue;

                    synth.Add(new SynthObs(
                        apiKeyId, tripId, new DateTime(ticks, DateTimeKind.Utc),
                        lat, lon, speedMs, (float)accuracy,
                        edge.Edge.OsmWayId,
                        edge.Edge.MaxSpeedKmh > 0 ? edge.Edge.MaxSpeedKmh : null));
                }
            }
            perSegmentSynth[bi] = synth;
        });

        // Flatten point matches
        var totalMatched = perSegmentMatched.Sum(m => m.Count);
        var matchedIds = new List<long>(totalMatched);
        var matchedWayIds = new List<long>(totalMatched);
        var matchedLimits = new List<double?>(totalMatched);



        var distinctOsmWayIds = perSegmentSynth.SelectMany(m => m).DistinctBy(s => s.WayId).Select(s => s.WayId);
        var speed_limits = await readOnlyAppDb.WayGeometries.Where(g => distinctOsmWayIds.Contains(g.OsmWayId)).Select(g => new { g.OsmWayId, g.MaxSpeedKmh }).ToDictionaryAsync(g => g.OsmWayId, g => g.MaxSpeedKmh);

        for (int bi = 0; bi < allSegments.Count; bi++)
        {
            var tripIdx = allSegments[bi].TripIndex;
            foreach (var (id, wayId, limit) in perSegmentMatched[bi])
            {
                matchedIds.Add(id);
                matchedWayIds.Add(wayId);
                var speed_limit = limit;
                if (speed_limit == null && speed_limits.ContainsKey(wayId))
                    speed_limit = speed_limits[wayId];
                matchedLimits.Add(speed_limit);
                tripMatchedIds[tripIdx].Add(id);
            }
        }




        // Every observation that stayed unmatched in this attempt.
        var unmatchedIds = new List<long>();
        for (int i = 0; i < trips.Count; i++)
        {
            foreach (var obs in trips[i])
            {
                if (!tripMatchedIds[i].Contains(obs.Id))
                    unmatchedIds.Add(obs.Id);
            }
        }

        if (matchedIds.Count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "SpeedObservations" o
                SET    "OsmWayId"      = v.way_id,
                       "SpeedLimitKmh" = v.speed_limit
                FROM   unnest(@ids, @way_ids, @limits) AS v(obs_id, way_id, speed_limit)
                WHERE  o."Id" = v.obs_id
                """,
                new object[]
                {
                    new NpgsqlParameter("ids",     NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = matchedIds.ToArray() },
                    new NpgsqlParameter("way_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = matchedWayIds.ToArray() },
                    new NpgsqlParameter("limits",  NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = matchedLimits.ToArray() },
                },
                ct);
        }

        if (unmatchedIds.Count > 0)
        {
            // Record the attempt. The trip is picked up again only when it receives new points
            // (MatchAttempts = 0). Give up when the attempt limit or the retry window is exceeded;
            // given-up rows (OsmWayId NULL, IsAggregated TRUE) are deleted by FinalizeAsync.
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "SpeedObservations"
                SET    "MatchAttempts" = "MatchAttempts" + 1,
                       "IsAggregated"  = ("MatchAttempts" + 1 >= @max_attempts OR "CreatedAt" < @stale_cutoff)
                WHERE  "Id" = ANY(@ids)
                """,
                new object[]
                {
                    new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = unmatchedIds.ToArray() },
                    new NpgsqlParameter("max_attempts", NpgsqlDbType.Integer) { Value = _maxMatchAttempts },
                    new NpgsqlParameter("stale_cutoff", NpgsqlDbType.TimestampTz) { Value = staleCutoff },
                },
                ct);
        }

        // ── Interpolated edges: INSERT synthetic observations ────────────────────
        // (Per-segment buffers were populated above in parallel; flatten here.)
        var totalSynth = perSegmentSynth.Sum(s => s.Count);
        var synthApiKeys = new List<int>(totalSynth);
        var synthTripIds = new List<string>(totalSynth);
        var synthTimestamp = new List<DateTime>(totalSynth);
        var synthLats = new List<double>(totalSynth);
        var synthLons = new List<double>(totalSynth);
        var synthSpeeds = new List<double>(totalSynth);
        var synthAccuracy = new List<float>(totalSynth);
        var synthWayIds = new List<long>(totalSynth);
        var synthLimits = new List<double?>(totalSynth);

        foreach (var bucket in perSegmentSynth)
        {
            foreach (var s in bucket)
            {
                synthApiKeys.Add(s.ApiKeyId);
                synthTripIds.Add(s.TripId);
                synthTimestamp.Add(s.Timestamp);
                synthLats.Add(s.Lat);
                synthLons.Add(s.Lon);
                synthSpeeds.Add(s.SpeedMs);
                synthAccuracy.Add(s.Accuracy);
                synthWayIds.Add(s.WayId);
                synthLimits.Add(s.SpeedLimit);
            }
        }

        if (synthWayIds.Count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "SpeedObservations"
                    ("ApiKeyId", "TripId", "Timestamp", "Latitude", "Longitude",
                     "SpeedMs", "GpsAccuracyMeters", "OsmWayId", "SpeedLimitKmh",
                     "IsAggregated", "CreatedAt")
                SELECT v.api_key_id, v.trip_id, v.ts, v.lat, v.lon,
                       v.speed_ms, v.gps_accuracy, v.way_id, v.speed_limit,
                       FALSE, NOW()
                FROM unnest(@api_key_ids, @trip_ids, @timestamps, @lats, @lons,
                            @speeds, @accuracies, @way_ids, @speed_limits)
                     AS v(api_key_id, trip_id, ts, lat, lon,
                          speed_ms, gps_accuracy, way_id, speed_limit)
                """,
                [
                    new NpgsqlParameter("api_key_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)     { Value = synthApiKeys.ToArray()   },
                    new NpgsqlParameter("trip_ids",    NpgsqlDbType.Array | NpgsqlDbType.Text)         { Value = synthTripIds.ToArray()   },
                    new NpgsqlParameter("timestamps",  NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)  { Value = synthTimestamp.ToArray() },
                    new NpgsqlParameter("lats",        NpgsqlDbType.Array | NpgsqlDbType.Double)       { Value = synthLats.ToArray()      },
                    new NpgsqlParameter("lons",        NpgsqlDbType.Array | NpgsqlDbType.Double)       { Value = synthLons.ToArray()      },
                    new NpgsqlParameter("speeds",      NpgsqlDbType.Array | NpgsqlDbType.Double)       { Value = synthSpeeds.ToArray()    },
                    new NpgsqlParameter("accuracies",  NpgsqlDbType.Array | NpgsqlDbType.Real)         { Value = synthAccuracy.ToArray()  },
                    new NpgsqlParameter("way_ids",     NpgsqlDbType.Array | NpgsqlDbType.Bigint)       { Value = synthWayIds.ToArray()    },
                    new NpgsqlParameter("speed_limits",NpgsqlDbType.Array | NpgsqlDbType.Double)       { Value = synthLimits.ToArray()    },
                ],
                ct);
            _logInterpolated(logger, synthWayIds.Count, null);
        }

        var total = matchedIds.Count + synthWayIds.Count;
        _logMapMatched(logger, total, null);
        var distinctWayIds = matchedWayIds.Concat(synthWayIds).Distinct().ToArray();

        // Trips where zero observations were matched (neither point-match nor interpolated).
        var tripHasMatch = new bool[trips.Count];
        for (int bi = 0; bi < allSegments.Count; bi++)
        {
            var (tripIdx, _, _) = allSegments[bi];
            var result = bi < rawResults.Length ? rawResults[bi] : null;
            if (result?.MatchedPoints?.Count > 0 || result?.Edges?.Count > 0)
                tripHasMatch[tripIdx] = true;
        }
        var failedTripIds = trips
            .Where((_, i) => !tripHasMatch[i])
            .Select(g => g.Key)
            .ToArray();

        return new MatchJobResult(total, distinctWayIds, failedTripIds);
    }

    /// <summary>
    /// Splits a sorted observation list into sub-segments suitable for map matching.
    /// A new segment starts when:
    ///   - the distance between consecutive points exceeds 500 m, or
    ///   - the accumulated path length exceeds 20,000 m.
    /// </summary>
    private static List<List<SpeedObservation>> SplitTrace(List<SpeedObservation> sorted)
    {
        const double maxSegmentMeters = 20_000;
        const double maxGapMeters = 500;

        var segments = new List<List<SpeedObservation>>();
        var current = new List<SpeedObservation>([sorted[0]]);
        double length = 0;

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            var pt = sorted[i];
            double d = HaversineMeters(prev.Latitude, prev.Longitude, pt.Latitude, pt.Longitude);

            if (d > maxGapMeters || length + d > maxSegmentMeters)
            {
                segments.Add(current);
                current = new List<SpeedObservation>();
                length = 0;
            }

            current.Add(pt);
            length += d;
        }

        segments.Add(current);
        return segments;
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string EncodePolyline6(IEnumerable<(double Lat, double Lon, DateTime Time)> coords) =>
        PolylineEncoder.Encode(coords.Select(c => (c.Lat, c.Lon)));

    private static readonly HashSet<string> _excludedRoadTypes =
    [
        "driveway", "parking_aisle", "emergency_access", "drive_through_road",
        "footway", "cycleway", "path", "steps", "pedestrian", "service"
    ];

    private static readonly HashSet<string> _allowedRoadTypes =
    [
        "motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential",
        "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link"
    ];

    private static bool IsExcludedRoadType(string? roadType) =>
        roadType != null &&
        (_excludedRoadTypes.Contains(roadType) || !_allowedRoadTypes.Contains(roadType));

    /// <summary>
    /// Extracts direct point matches from a TraceResult.
    /// Returns a dictionary mapping original_index → MatchResult.
    /// </summary>
    private Dictionary<int, MatchResult> ParsePointMatches(TraceResult? result)
    {
        if (result?.MatchedPoints == null || result.Edges == null) return [];

        // Build fallback: point-index → edge info from non-interpolated edge coverage ranges.
        var pointToEdge = new Dictionary<int, (long WayId, int SpeedKmh)>();
        foreach (var edge in result.Edges)
        {
            if (edge.IsInterpolated) continue;
            for (int i = edge.BeginPointIndex; i <= edge.EndPointIndex; i++)
                pointToEdge[i] = (edge.Edge?.OsmWayId ?? 0, edge.Edge?.MaxSpeedKmh ?? 0);
        }

        var results = new Dictionary<int, MatchResult>();
        int badType = 0, noEdge = 0, excluded = 0;

        for (int i = 0; i < result.MatchedPoints.Count; i++)
        {
            var mp = result.MatchedPoints[i];
            if (mp.MatchType != "matched" && mp.MatchType != "interpolated")
            {
                badType++;
                continue;
            }

            var edgeInfo = mp.Edge;
            if ((edgeInfo?.OsmWayId ?? 0) == 0 && pointToEdge.TryGetValue(i, out var re) && re.WayId != 0)
                edgeInfo = new TraceEdgeInfo { OsmWayId = re.WayId, MaxSpeedKmh = re.SpeedKmh };

            if (edgeInfo == null || edgeInfo.OsmWayId == 0) { noEdge++; continue; }

            if (IsExcludedRoadType(edgeInfo.RoadType)) { excluded++; continue; }

            results[mp.OriginalIndex] = new MatchResult(
                OsmWayId: edgeInfo.OsmWayId,
                SpeedLimitKmh: edgeInfo.MaxSpeedKmh > 0 ? edgeInfo.MaxSpeedKmh : null);
        }

        if (badType + noEdge + excluded > 0)
            _logParseSkip(logger, result.MatchedPoints.Count, results.Count, badType, noEdge, excluded, null);

        return results;
    }

    // --- OpenTrafficRouting trace response DTOs (mirrors OTR TraceModels) ---

    private class BulkTraceResponse
    {
        [JsonPropertyName("results")]
        public List<TraceResult>? Results { get; set; }
    }

    public class TraceResult
    {
        [JsonPropertyName("edges")]
        public List<TraceRouteEdge>? Edges { get; set; }

        [JsonPropertyName("matched_points")]
        public List<TraceMatchedPoint>? MatchedPoints { get; set; }
    }

    public class TraceRouteEdge
    {
        [JsonPropertyName("begin_point_index")]
        public int BeginPointIndex { get; set; }

        [JsonPropertyName("end_point_index")]
        public int EndPointIndex { get; set; }

        [JsonPropertyName("is_interpolated")]
        public bool IsInterpolated { get; set; }

        [JsonPropertyName("interpolation_references")]
        public List<InterpolationReference>? InterpolationReferences { get; set; }

        [JsonPropertyName("edge")]
        public TraceEdgeInfo? Edge { get; set; }

        [JsonPropertyName("node")]
        public TraceNodeInfo? Node { get; set; }
    }

    public class InterpolationReference
    {
        [JsonPropertyName("point_index")]
        public int PointIndex { get; set; }

        [JsonPropertyName("percentage")]
        public double Percentage { get; set; }
    }

    public class TraceMatchedPoint
    {
        [JsonPropertyName("match_type")]
        public string MatchType { get; set; } = "unmatched";

        [JsonPropertyName("original_index")]
        public int OriginalIndex { get; set; }

        [JsonPropertyName("edge")]
        public TraceEdgeInfo? Edge { get; set; }

        [JsonPropertyName("node")]
        public TraceNodeInfo? Node { get; set; }
    }

    public class TraceEdgeInfo
    {
        [JsonPropertyName("osm_way_id")]
        public long OsmWayId { get; set; }

        [JsonPropertyName("max_speed_kmh")]
        public int MaxSpeedKmh { get; set; }

        [JsonPropertyName("road_type")]
        public string? RoadType { get; set; }
    }

    public class TraceNodeInfo
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}
