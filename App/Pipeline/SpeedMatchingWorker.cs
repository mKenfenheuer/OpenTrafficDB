using Microsoft.Extensions.Caching.Memory;
using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>
/// Streams trips from the <see cref="SpeedPipeline"/> to OpenTrafficRouting and turns the
/// results into one <see cref="WaySample"/> per trip and road.
///
/// Up to <c>OpenTrafficRouting:MaxConcurrent</c> bulk requests of
/// <c>OpenTrafficRouting:BatchSize</c> polylines are in flight at any time. A new batch starts
/// as soon as a slot is free, so the matcher works continuously instead of in passes.
///
/// Match results are cached per polyline (<c>Pipeline:MatchCacheEntries</c>, sliding
/// <c>Pipeline:MatchCacheHours</c>). Sources with fixed geometry, such as the TomTom flow
/// segments that repeat every cycle with new speeds only, are matched once and then served
/// from the cache, so OpenTrafficRouting (CPU-bound) only sees new geometry.
///
/// Per trip and road, the matched points are averaged into one sample (speed, time, limit).
/// Roads crossed between two points (interpolated edges) get one sample each, unless the trip
/// already has a point on them. This "one sample per traversal" keeps the aggregation volume
/// independent of the GPS sampling rate.
/// </summary>
public sealed class SpeedMatchingWorker(
    SpeedPipeline pipeline,
    SpeedAggregator aggregator,
    MapMatchingService matcher,
    IConfiguration config,
    ILogger<SpeedMatchingWorker> logger) : BackgroundService
{
    private const double MaxGapMeters = 500;
    private const double MaxSegmentMeters = 20_000;
    private const int MaxPolylinesPerRequest = 1000;   // OpenTrafficRouting limit

    private readonly int _batchSize = int.TryParse(config["OpenTrafficRouting:BatchSize"], out var bs) ? bs : 200;
    private readonly int _concurrency = int.TryParse(config["OpenTrafficRouting:MaxConcurrent"], out var mc) ? mc : 8;
    private readonly TimeSpan _linger = TimeSpan.FromMilliseconds(
        double.TryParse(config["Pipeline:BatchLingerMs"], out var lm) ? lm : 100);

    /// <summary>A trace segment and the OSM road types it may be matched onto (see <see cref="RoadClassFilter"/>).</summary>
    private sealed record Segment(List<(SpeedPoint Point, bool IsContext)> Points, string Polyline, string[] RoadTypes, string CacheKey);

    /// <summary>Geometry-only match result of one polyline: which point lies on which road.</summary>
    private sealed record MatchPlan(
        int[] PointIndex, long[][] PointWays, double?[] PointLimit, short[] PointClass, InterpolatedWay[] Interpolated);

    /// <summary>An interpolated edge: all OSM ways it covers (a shortcut covers several).</summary>
    private sealed record InterpolatedWay(long[] WayIds, double? Limit, short RoadClass, int[] PointIndex, double[] Percentage);

    private readonly MemoryCache _planCache = new(new MemoryCacheOptions
    {
        SizeLimit = long.TryParse(config["Pipeline:MatchCacheEntries"], out var ce) ? ce : 3_000_000
    });
    private readonly TimeSpan _planTtl = TimeSpan.FromHours(
        double.TryParse(config["Pipeline:MatchCacheHours"], out var ch) ? ch : 12);

    private long _cacheHits, _cacheMisses;
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    private sealed record TripWork(
        SpeedPipeline.TripState State, List<SpeedPoint> Context, List<SpeedPoint> Pending, List<Segment> Segments);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Speed matching worker started. BatchSize={BatchSize}, Concurrency={Concurrency}.",
            _batchSize, _concurrency);

        var evictor = EvictLoopAsync(stoppingToken);
        var slots = new SemaphoreSlim(_concurrency, _concurrency);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                List<TripWork> batch;
                try
                {
                    batch = await CollectBatchAsync(stoppingToken);
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                _ = Task.Run(async () =>
                {
                    try { await RunBatchAsync(batch, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception ex) { logger.LogError(ex, "Speed matching batch failed."); }
                    finally { slots.Release(); }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        await evictor;
    }

    private async Task EvictLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var evicted = pipeline.EvictIdleTrips();
                logger.LogInformation(
                    "Pipeline: {Buffered} points buffered, {Trips} trips tracked ({Evicted} evicted), match cache {Hits} hits / {Misses} misses, {Pending} samples pending.",
                    pipeline.BufferedPoints, pipeline.TrackedTrips, evicted, CacheHits, CacheMisses, aggregator.PendingSamples);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Waits for the first ready trip, then collects more until the batch holds
    /// <c>BatchSize</c> polylines or no further trip arrives within the linger time.
    /// </summary>
    private async Task<List<TripWork>> CollectBatchAsync(CancellationToken ct)
    {
        var batch = new List<TripWork>();
        var polylines = 0;
        var reader = pipeline.Ready;

        var first = await reader.ReadAsync(ct);
        Add(first);

        var deadline = DateTime.UtcNow + _linger;
        while (polylines < _batchSize)
        {
            if (reader.TryRead(out var next)) { Add(next); continue; }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(remaining);
            try
            {
                if (!await reader.WaitToReadAsync(timeout.Token)) break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }

        return batch;

        void Add(SpeedPipeline.TripState state)
        {
            var (context, pending) = pipeline.Take(state);
            if (pending.Count == 0) return;
            var segments = BuildSegments(context, pending);
            batch.Add(new TripWork(state, context, pending, segments));
            polylines += segments.Count;
        }
    }

    /// <summary>
    /// Orders context and new points by time and splits them where consecutive points are more
    /// than 500 m apart or the accumulated length exceeds 20 km. Segments without new points are skipped.
    /// </summary>
    private static List<Segment> BuildSegments(List<SpeedPoint> context, List<SpeedPoint> pending)
    {
        var all = new List<(SpeedPoint Point, bool IsContext)>(context.Count + pending.Count);
        all.AddRange(context.Select(p => (p, true)));
        all.AddRange(pending.Select(p => (p, false)));
        all.Sort((a, b) =>
        {
            var c = a.Point.Timestamp.CompareTo(b.Point.Timestamp);
            return c != 0 ? c : a.Point.Seq.CompareTo(b.Point.Seq);
        });

        var segments = new List<Segment>();
        var current = new List<(SpeedPoint Point, bool IsContext)> { all[0] };
        double length = 0;

        for (int i = 1; i < all.Count; i++)
        {
            var prev = all[i - 1].Point;
            var pt = all[i].Point;
            var d = MapMatchingService.HaversineMeters(prev.Latitude, prev.Longitude, pt.Latitude, pt.Longitude);
            if (d > MaxGapMeters || length + d > MaxSegmentMeters)
            {
                Flush();
                current = [];
                length = 0;
            }
            current.Add(all[i]);
            length += d;
        }
        Flush();
        return segments;

        void Flush()
        {
            if (current.Count == 0 || current.All(p => p.IsContext)) return;
            var coords = current.Select(p => (p.Point.Latitude, p.Point.Longitude, p.Point.Timestamp)).ToList();
            if (coords.Count == 1) coords.Add(coords[0]);
            var roadTypes = RoadTypesFor(current);
            var polyline = MapMatchingService.EncodePolyline6(coords);
            segments.Add(new Segment(current, polyline, roadTypes, string.Join(',', roadTypes) + "|" + polyline));
        }
    }

    private MatchPlan BuildPlan(MapMatchingService.TraceResult result)
    {
        var points = matcher.ParsePointMatches(result);
        var idx = new int[points.Count]; var ways = new long[points.Count][]; var limits = new double?[points.Count];
        var classes = new short[points.Count];
        var k = 0;
        foreach (var (i, m) in points) { idx[k] = i; ways[k] = m.WayIds; limits[k] = m.SpeedLimitKmh; classes[k] = m.RoadClass; k++; }

        var interpolated = new List<InterpolatedWay>();
        if (result.Edges != null && result.MatchedPoints != null)
        {
            var seen = new HashSet<long>();
            foreach (var edge in result.Edges)
            {
                if (!edge.IsInterpolated || edge.Edge == null || edge.InterpolationReferences == null
                    || edge.Edge.OsmWayId == 0 || MapMatchingService.IsExcludedRoadType(edge.Edge.RoadType)
                    || !seen.Add(edge.Edge.OsmWayId)) continue;

                var refs = edge.InterpolationReferences
                    .Where(r => r.PointIndex < result.MatchedPoints.Count)
                    .Select(r => (Orig: result.MatchedPoints[r.PointIndex].OriginalIndex, r.Percentage))
                    .ToArray();
                if (refs.Length == 0) continue;
                interpolated.Add(new InterpolatedWay(MapMatchingService.WayIdsOf(edge.Edge),
                    edge.Edge.MaxSpeedKmh > 0 ? edge.Edge.MaxSpeedKmh : null,
                    MapMatchingService.RoadClassOf(edge.Edge.RoadType),
                    refs.Select(r => r.Orig).ToArray(), refs.Select(r => r.Percentage).ToArray()));
            }
        }
        return new MatchPlan(idx, ways, limits, classes, [.. interpolated]);
    }

    /// <summary>
    /// Allowed road types for a segment: the explicit class hint of its new points if they carry one,
    /// otherwise inferred from their median speed.
    /// </summary>
    private static string[] RoadTypesFor(List<(SpeedPoint Point, bool IsContext)> segment)
    {
        var fresh = segment.Where(p => !p.IsContext).Select(p => p.Point).ToList();
        var hint = fresh.Where(p => p.RoadClassHint != RoadClassFilter.None)
            .GroupBy(p => p.RoadClassHint).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (hint != RoadClassFilter.None) return RoadClassFilter.ForHint(hint);

        var speeds = fresh.Select(p => p.SpeedMs * 3.6).OrderBy(v => v).ToList();
        return speeds.Count == 0 ? RoadClassFilter.All : RoadClassFilter.ForSpeed(speeds[speeds.Count / 2]);
    }

    private async Task RunBatchAsync(List<TripWork> batch, CancellationToken ct)
    {
        // Resolve match plans from the cache; only polylines with new geometry go to OpenTrafficRouting.
        var allSegments = batch.SelectMany(t => t.Segments).ToList();
        var plans = new MatchPlan?[allSegments.Count];
        var misses = new List<int>();
        for (int i = 0; i < allSegments.Count; i++)
        {
            if (_planCache.TryGetValue(allSegments[i].CacheKey, out MatchPlan? cached)) plans[i] = cached;
            else misses.Add(i);
        }
        Interlocked.Add(ref _cacheHits, allSegments.Count - misses.Count);
        Interlocked.Add(ref _cacheMisses, misses.Count);

        // One request per allowed-road-type set (OpenTrafficRouting filters per request).
        var unreachable = new bool[allSegments.Count];
        bool anyUnreachable = false;
        foreach (var group in misses.GroupBy(m => string.Join(',', allSegments[m].RoadTypes)))
        for (int i = 0; i < group.Count(); i += MaxPolylinesPerRequest)
        {
            var chunk = group.Skip(i).Take(MaxPolylinesPerRequest).ToList();
            var results = await matcher.MatchTracesBulkAsync(
                chunk.Select(m => allSegments[m].Polyline).ToList(), allSegments[chunk[0]].RoadTypes, ct);
            if (results.All(r => r == null))
            {
                // OpenTrafficRouting not reachable (restart, overload): keep these points without
                // counting an attempt, so an outage neither drops data nor spins through retries.
                foreach (var m in chunk) unreachable[m] = true;
                anyUnreachable = true;
                continue;
            }
            for (int j = 0; j < chunk.Count && j < results.Count; j++)
            {
                if (results[j] == null) continue;   // transport error: not cached, retried with the trip's next points
                var plan = BuildPlan(results[j]!);
                plans[chunk[j]] = plan;
                _planCache.Set(allSegments[chunk[j]].CacheKey, plan,
                    new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = _planTtl });
            }
        }

        var samples = new List<WaySample>();
        var now = DateTime.UtcNow;
        var segmentIndex = 0;

        foreach (var trip in batch)
        {
            var matchedSeqs = new HashSet<long>();
            bool tripUnreachable = false;
            var pointWays = new Dictionary<long, (double SpeedSum, long TicksSum, int Count, double? Limit, short Class)>();
            var interpolatedWays = new Dictionary<long, (double SpeedMs, long Ticks, double? Limit, short Class)>();

            foreach (var segment in trip.Segments)
            {
                if (unreachable[segmentIndex]) tripUnreachable = true;
                var plan = plans[segmentIndex++];
                if (plan == null) continue;

                for (int k = 0; k < plan.PointIndex.Length; k++)
                {
                    var idx = plan.PointIndex[k];
                    if (idx >= segment.Points.Count) continue;
                    var (point, isContext) = segment.Points[idx];
                    if (isContext || !matchedSeqs.Add(point.Seq)) continue;

                    // A point on a shortcut edge counts for every way the edge covers.
                    foreach (var wayId in plan.PointWays[k])
                    {
                        pointWays.TryGetValue(wayId, out var acc);
                        pointWays[wayId] = (acc.SpeedSum + point.SpeedMs, acc.TicksSum + point.Timestamp.Ticks / 1000,
                            acc.Count + 1, acc.Limit ?? plan.PointLimit[k], plan.PointClass[k]);
                    }
                }

                foreach (var way in plan.Interpolated)
                {
                    if (way.WayIds.All(interpolatedWays.ContainsKey)) continue;
                    double speed = 0, ticks = 0, weight = 0;
                    var newData = false;
                    for (int r = 0; r < way.PointIndex.Length; r++)
                    {
                        var orig = way.PointIndex[r];
                        if (orig >= segment.Points.Count) continue;
                        var (point, isContext) = segment.Points[orig];
                        newData |= !isContext;
                        speed += way.Percentage[r] * point.SpeedMs;
                        ticks += way.Percentage[r] * point.Timestamp.Ticks;
                        weight += way.Percentage[r];
                    }
                    if (!newData || weight <= 0) continue;
                    foreach (var wayId in way.WayIds)
                        interpolatedWays.TryAdd(wayId, (speed / weight, (long)(ticks / weight), way.Limit, way.RoadClass));
                }
            }

            foreach (var (wayId, acc) in pointWays)
                samples.Add(new WaySample(wayId, new DateTime(acc.TicksSum / acc.Count * 1000, DateTimeKind.Utc),
                    acc.SpeedSum / acc.Count, acc.Limit, acc.Class));
            foreach (var (wayId, s) in interpolatedWays)
                if (!pointWays.ContainsKey(wayId))
                    samples.Add(new WaySample(wayId, new DateTime(s.Ticks, DateTimeKind.Utc), s.SpeedMs, s.Limit, s.Class));

            // Unmatched points: retry with the trip's next points, within the attempt limit and retry window.
            var retry = new List<SpeedPoint>();
            foreach (var p in trip.Pending)
            {
                if (matchedSeqs.Contains(p.Seq)) continue;
                if (now - p.ReceivedAt >= pipeline.MatchRetryWindow) continue;
                if (tripUnreachable) retry.Add(p);
                else if (p.Attempts + 1 < pipeline.MaxMatchAttempts)
                    retry.Add(p with { Attempts = (short)(p.Attempts + 1) });
            }

            var newContext = trip.Pending
                .Where(p => matchedSeqs.Contains(p.Seq))
                .OrderBy(p => p.Timestamp).ThenBy(p => p.Seq)
                .TakeLast(pipeline.ContextPoints)
                .ToList();

            pipeline.Complete(trip.State, trip.Pending.Count, matchedSeqs.Count, retry, newContext);
        }

        aggregator.Add(samples);

        // Back off while OpenTrafficRouting is unreachable (this batch keeps its slot meanwhile).
        if (anyUnreachable)
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
}
