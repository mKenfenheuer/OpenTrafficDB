using System.Collections.Concurrent;
using System.Threading.Channels;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>A GPS speed observation waiting in the in-memory pipeline.</summary>
public readonly record struct SpeedPoint(
    DateTime Timestamp,
    double Latitude,
    double Longitude,
    double SpeedMs,
    float GpsAccuracyMeters,
    DateTime ReceivedAt,
    long Seq,
    short Attempts = 0,
    byte RoadClassHint = 0);

/// <summary>
/// In-memory buffer between ingestion and map matching. Raw observations never touch the
/// database: they wait here per trip until <see cref="SpeedMatchingWorker"/> matches them,
/// and only the aggregated results are written to Postgres (<see cref="SpeedAggregator"/>).
///
/// <list type="bullet">
/// <item>A trip is queued for matching when it receives new points. Points that stayed
/// unmatched are retried only together with new points of the same trip (up to
/// <c>Aggregation:MaxMatchAttempts</c>, within <c>Aggregation:MatchRetryWindowSeconds</c>).</item>
/// <item>The last matched points of a trip are kept as context, so a trip that arrives in
/// several uploads is matched as one continuous trace.</item>
/// <item>The buffer is bounded (<c>Pipeline:MaxBufferedPoints</c>). When it is full,
/// <see cref="TryEnqueue"/> refuses new points and the API answers 503 with Retry-After.</item>
/// <item>Data in the buffer is lost on restart. At a TomTom refresh of ~24 min this is a few
/// seconds of data; senders can resubmit.</item>
/// </list>
/// </summary>
public sealed class SpeedPipeline(IConfiguration config)
{
    internal sealed class TripState(string key)
    {
        public string Key { get; } = key;
        public readonly object Lock = new();
        public List<SpeedPoint> Pending = [];
        public List<SpeedPoint> Context = [];
        public DateTime LastActivity = DateTime.UtcNow;
        public bool Queued;
    }

    private readonly ConcurrentDictionary<string, TripState> _trips = new();
    private readonly Channel<TripState> _ready = Channel.CreateUnbounded<TripState>(
        new UnboundedChannelOptions { SingleReader = true });

    private long _seq;
    private long _bufferedPoints;
    private long _ingested;
    private long _processed;
    private long _matched;
    private long _dropped;
    private long _rejected;

    public long MaxBufferedPoints { get; } = long.TryParse(config["Pipeline:MaxBufferedPoints"], out var m) ? m : 3_000_000;
    public int MaxMatchAttempts { get; } = int.TryParse(config["Aggregation:MaxMatchAttempts"], out var a) ? a : 3;
    public TimeSpan MatchRetryWindow { get; } = TimeSpan.FromSeconds(
        double.TryParse(config["Aggregation:MatchRetryWindowSeconds"], out var r) ? r : 300);
    public TimeSpan TripIdleTimeout { get; } = TimeSpan.FromSeconds(
        double.TryParse(config["Pipeline:TripIdleSeconds"], out var t) ? t : 180);
    public int ContextPoints { get; } = int.TryParse(config["Pipeline:ContextPoints"], out var c) ? c : 3;

    /// <summary>Points waiting for or currently in map matching.</summary>
    public long BufferedPoints => Interlocked.Read(ref _bufferedPoints);
    /// <summary>Trips currently tracked (including idle trips kept for context).</summary>
    public int TrackedTrips => _trips.Count;
    /// <summary>Cumulative number of accepted points.</summary>
    public long IngestedTotal => Interlocked.Read(ref _ingested);
    /// <summary>Cumulative number of points that finished a matching attempt.</summary>
    public long ProcessedTotal => Interlocked.Read(ref _processed);
    /// <summary>Cumulative number of points matched to a road.</summary>
    public long MatchedTotal => Interlocked.Read(ref _matched);
    /// <summary>Cumulative number of points given up (unmatched after all attempts, or trip idle).</summary>
    public long DroppedTotal => Interlocked.Read(ref _dropped);
    /// <summary>Cumulative number of points refused because the buffer was full.</summary>
    public long RejectedTotal => Interlocked.Read(ref _rejected);

    internal ChannelReader<TripState> Ready => _ready.Reader;

    /// <summary>
    /// Adds the points of one trip. Returns false (and accepts nothing) when the buffer is full.
    /// </summary>
    public bool TryEnqueue(string tripKey, IReadOnlyList<(DateTime Timestamp, double Lat, double Lon, double SpeedMs, float Accuracy, byte RoadClassHint)> points)
    {
        if (points.Count == 0) return true;

        if (Interlocked.Add(ref _bufferedPoints, points.Count) > MaxBufferedPoints)
        {
            Interlocked.Add(ref _bufferedPoints, -points.Count);
            Interlocked.Add(ref _rejected, points.Count);
            return false;
        }

        var now = DateTime.UtcNow;
        var state = _trips.GetOrAdd(tripKey, k => new TripState(k));
        var enqueue = false;
        lock (state.Lock)
        {
            foreach (var p in points)
                state.Pending.Add(new SpeedPoint(p.Timestamp, p.Lat, p.Lon, p.SpeedMs, p.Accuracy, now,
                    Interlocked.Increment(ref _seq), 0, p.RoadClassHint));
            state.LastActivity = now;
            if (!state.Queued)
            {
                state.Queued = true;
                enqueue = true;
            }
        }

        Interlocked.Add(ref _ingested, points.Count);
        if (enqueue) _ready.Writer.TryWrite(state);
        return true;
    }

    /// <summary>Has room for <paramref name="count"/> more points.</summary>
    public bool HasCapacity(int count) => BufferedPoints + count <= MaxBufferedPoints;

    /// <summary>
    /// Takes the pending points of a queued trip for matching, together with its context points.
    /// The trip can be queued again by new points while the batch is in flight.
    /// </summary>
    internal (List<SpeedPoint> Context, List<SpeedPoint> Pending) Take(TripState state)
    {
        lock (state.Lock)
        {
            state.Queued = false;
            var pending = state.Pending;
            state.Pending = [];
            return (state.Context.ToList(), pending);
        }
    }

    /// <summary>
    /// Completes a matching attempt: unmatched points that may be retried go back to the trip
    /// (without queueing it), the newest matched points become the trip's context.
    /// </summary>
    internal void Complete(TripState state, int attempted, int matched,
        List<SpeedPoint> retry, List<SpeedPoint> newContext)
    {
        lock (state.Lock)
        {
            if (retry.Count > 0)
            {
                retry.AddRange(state.Pending);
                state.Pending = retry;
            }
            if (newContext.Count > 0)
                state.Context = newContext;
            state.LastActivity = DateTime.UtcNow;
        }

        Interlocked.Add(ref _bufferedPoints, -(attempted - retry.Count));
        Interlocked.Add(ref _processed, attempted);
        Interlocked.Add(ref _matched, matched);
        Interlocked.Add(ref _dropped, attempted - matched - retry.Count);
    }

    /// <summary>
    /// Forgets trips without activity for <see cref="TripIdleTimeout"/>; their leftover points are dropped.
    /// </summary>
    internal int EvictIdleTrips()
    {
        var cutoff = DateTime.UtcNow - TripIdleTimeout;
        var evicted = 0;
        foreach (var (key, state) in _trips)
        {
            if (state.LastActivity >= cutoff) continue;
            int leftover;
            lock (state.Lock)
            {
                if (state.Queued || state.LastActivity >= cutoff) continue;
                leftover = state.Pending.Count;
                state.Pending = [];
            }
            if (_trips.TryRemove(new KeyValuePair<string, TripState>(key, state)))
            {
                evicted++;
                if (leftover > 0)
                {
                    Interlocked.Add(ref _bufferedPoints, -leftover);
                    Interlocked.Add(ref _dropped, leftover);
                }
            }
        }
        return evicted;
    }
}
