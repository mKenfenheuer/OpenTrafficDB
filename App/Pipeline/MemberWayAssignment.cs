using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>
/// Narrows the ways of points on compacted router edges to the ways the trace actually runs along.
///
/// OpenTrafficRouting merges consecutive OSM ways of one road into a single edge (junction to
/// junction, up to about 10 km and 20 ways) and reports all of them for every point on it. A
/// TomTom segment of 500 m in a jam then slowed the whole 10 km down, and a segment that
/// covered only the start of the edge wrote its speed onto ways it never touched.
///
/// Per edge (the points that share one way set):
/// <list type="number">
/// <item>Each point counts for the member way nearest to it.</item>
/// <item>Each member way is sampled every <see cref="SampleMeters"/>; a sample within
/// <see cref="MaxDistanceMeters"/> of a trace segment counts for the nearer end point of that
/// segment if it lies on this edge (else the other end). Trace points can be far apart (TomTom
/// polylines have points only where the road bends), so ways between two points still get them.</item>
/// </list>
/// Member ways that no part of the trace comes near get no sample. Ways without known geometry
/// keep the old behaviour (all points of the edge).
/// </summary>
public static class MemberWayAssignment
{
    public const double MaxDistanceMeters = 50;
    public const double SampleMeters = 50;
    private const double MetersPerDegree = 111_320;

    /// <summary>Ways of edges that cover more than one way, from point matches and matched edges.</summary>
    public static HashSet<long> MemberWays(IEnumerable<MapMatchingService.TraceResult?> results)
    {
        var ids = new HashSet<long>();
        foreach (var r in results)
        {
            if (r == null) continue;
            foreach (var p in r.MatchedPoints ?? []) Add(p.Edge);
            foreach (var e in r.Edges ?? []) if (!e.IsInterpolated) Add(e.Edge);
        }
        return ids;

        void Add(MapMatchingService.TraceEdgeInfo? e)
        {
            if (e?.OsmWayIds is { Length: > 1 } w) ids.UnionWith(w);
        }
    }

    /// <summary>
    /// Replaces <paramref name="pointWays"/> entries with more than one way by the member ways next
    /// to that point. <paramref name="pointIndex"/> holds the index of each plan point in
    /// <paramref name="trace"/>.
    /// </summary>
    public static void Assign((double Lat, double Lon)[] trace, int[] pointIndex, long[][] pointWays,
        IReadOnlyDictionary<long, float[]> shapes)
    {
        var groups = new Dictionary<long[], List<int>>(WaySetComparer.Instance);
        for (int k = 0; k < pointWays.Length; k++)
        {
            if (pointWays[k].Length <= 1 || pointIndex[k] >= trace.Length) continue;
            if (!groups.TryGetValue(pointWays[k], out var list)) groups[pointWays[k]] = list = [];
            list.Add(k);
        }

        foreach (var (set, ks) in groups)
        {
            var assigned = ks.ToDictionary(k => k, _ => new HashSet<long>());
            var byTraceIndex = new Dictionary<int, int>(ks.Count);
            foreach (var k in ks) byTraceIndex.TryAdd(pointIndex[k], k);
            int lo = ks.Min(k => pointIndex[k]), hi = ks.Max(k => pointIndex[k]);

            var known = new List<(long Way, float[] Shape)>(set.Length);
            foreach (var w in set)
            {
                if (shapes.TryGetValue(w, out var s) && s.Length >= 4) known.Add((w, s));
                else foreach (var k in ks) assigned[k].Add(w);   // unknown geometry: as before
            }

            if (known.Count > 0)
            {
                // 1. Each point: its nearest member way.
                foreach (var k in ks)
                {
                    var (lat, lon) = trace[pointIndex[k]];
                    long best = 0; double bestD = double.MaxValue;
                    foreach (var (w, s) in known)
                    {
                        var d = DistanceToShape(s, lat, lon);
                        if (d < bestD) { bestD = d; best = w; }
                    }
                    assigned[k].Add(best);
                }

                // 2. Each member way: the trace segments it runs along.
                int jFrom = Math.Max(0, lo - 1), jTo = Math.Min(trace.Length - 2, hi);
                foreach (var (w, s) in known)
                    foreach (var (lat, lon) in Samples(s))
                    {
                        if (trace.Length == 1)
                        {
                            if (DistanceMeters(trace[0], lat, lon) <= MaxDistanceMeters && byTraceIndex.TryGetValue(0, out var only))
                                assigned[only].Add(w);
                            continue;
                        }
                        int bestJ = -1; double bestD = double.MaxValue, bestT = 0;
                        for (int j = jFrom; j <= jTo; j++)
                        {
                            var (d, t) = DistanceToSegment(trace[j], trace[j + 1], lat, lon);
                            if (d < bestD) { bestD = d; bestJ = j; bestT = t; }
                        }
                        if (bestJ < 0 || bestD > MaxDistanceMeters) continue;
                        int near = bestT < 0.5 ? bestJ : bestJ + 1, far = bestT < 0.5 ? bestJ + 1 : bestJ;
                        if (byTraceIndex.TryGetValue(near, out var kn)) assigned[kn].Add(w);
                        else if (byTraceIndex.TryGetValue(far, out var kf)) assigned[kf].Add(w);
                    }
            }

            foreach (var k in ks) pointWays[k] = [.. assigned[k]];
        }
    }

    /// <summary>Vertices of a shape plus intermediate points so no two are more than <see cref="SampleMeters"/> apart.</summary>
    private static IEnumerable<(double Lat, double Lon)> Samples(float[] s)
    {
        yield return (s[0], s[1]);
        for (int i = 2; i + 1 < s.Length; i += 2)
        {
            double aLat = s[i - 2], aLon = s[i - 1], bLat = s[i], bLon = s[i + 1];
            double len = DistanceMeters((aLat, aLon), bLat, bLon);
            int steps = Math.Max(1, (int)Math.Ceiling(len / SampleMeters));
            for (int k = 1; k <= steps; k++)
                yield return (aLat + (bLat - aLat) * k / steps, aLon + (bLon - aLon) * k / steps);
        }
    }

    private static double DistanceToShape(float[] s, double lat, double lon)
    {
        double best = double.MaxValue;
        for (int i = 2; i + 1 < s.Length; i += 2)
        {
            var (d, _) = DistanceToSegment((s[i - 2], s[i - 1]), (s[i], s[i + 1]), lat, lon);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Distance in metres from a point to segment a–b, and the position on it (0 = a, 1 = b).</summary>
    private static (double Meters, double T) DistanceToSegment((double Lat, double Lon) a, (double Lat, double Lon) b,
        double lat, double lon)
    {
        double cos = Math.Cos(lat * Math.PI / 180);
        double dx = (b.Lon - a.Lon) * cos, dy = b.Lat - a.Lat;
        double px = (lon - a.Lon) * cos, py = lat - a.Lat;
        double len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
        double ex = px - t * dx, ey = py - t * dy;
        return (Math.Sqrt(ex * ex + ey * ey) * MetersPerDegree, t);
    }

    private static double DistanceMeters((double Lat, double Lon) a, double lat, double lon)
    {
        double cos = Math.Cos(lat * Math.PI / 180);
        double dx = (lon - a.Lon) * cos, dy = lat - a.Lat;
        return Math.Sqrt(dx * dx + dy * dy) * MetersPerDegree;
    }

    /// <summary>Way sets compared by content (each matched point carries its own array).</summary>
    private sealed class WaySetComparer : IEqualityComparer<long[]>
    {
        public static readonly WaySetComparer Instance = new();
        public bool Equals(long[]? x, long[]? y) => x != null && y != null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(long[] a)
        {
            var h = new HashCode();
            foreach (var v in a) h.Add(v);
            return h.ToHashCode();
        }
    }
}
