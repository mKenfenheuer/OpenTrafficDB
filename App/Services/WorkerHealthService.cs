using System.Collections.Concurrent;

namespace OpenTrafficDB.Shared.Services;

public enum WorkerStatus { Unknown, Healthy, Degraded, Unhealthy }

/// <summary>
/// Per-instance (URL) metrics snapshot returned to the admin panel.
/// </summary>
public record WorkerInstanceInfo(
    string Url,
    WorkerStatus Status,
    DateTime? LastChecked,
    double? HealthCheckMs,
    // request metrics aggregated across all proxied calls
    long TotalRequests,
    long ErrorCount,
    double? AvgResponseMs,
    double? P95ResponseMs,
    double? LastResponseMs
);

/// <summary>
/// Service-level summary — one per named worker (Ingest / Aggregate / Routing).
/// </summary>
public record WorkerServiceInfo(
    string Name,
    WorkerStatus Status,          // worst-case across instances
    WorkerInstanceInfo[] Instances
);

/// <summary>
/// Background service that polls all workers every 10 s and accumulates
/// per-instance latency/error metrics from <see cref="WorkerClient"/> callbacks.
/// </summary>
public class WorkerHealthService(
    WorkerClient workerClient,
    ILogger<WorkerHealthService> logger) : BackgroundService
{
    private static readonly string[] WorkerNames = ["Ingest", "Aggregate", "Routing"];

    // keyed by "WorkerName|url"
    private readonly ConcurrentDictionary<string, InstanceState> _states = new();

    // ── Public API used by WorkerClient ───────────────────────────────────────

    /// <summary>
    /// Called by <see cref="WorkerClient"/> after every proxied request completes.
    /// </summary>
    public void RecordRequest(string workerName, string url, double elapsedMs, bool success)
    {
        var state = _states.GetOrAdd($"{workerName}|{url}", _ => new InstanceState());
        state.RecordRequest(elapsedMs, success);
    }

    // ── Public API used by admin controller ──────────────────────────────────

    public WorkerServiceInfo[] GetAllStatuses() =>
        WorkerNames.Select(name =>
        {
            var urls = workerClient.GetUrls(name) ?? [];
            var instances = urls.Select(url =>
            {
                var key = $"{name}|{url}";
                var s   = _states.TryGetValue(key, out var st) ? st : null;
                var (hcStatus, hcMs, lastChecked) = s is null
                    ? (WorkerStatus.Unknown, (double?)null, (DateTime?)null)
                    : (s.HealthStatus, s.LastHealthCheckMs, s.LastHealthChecked);

                return new WorkerInstanceInfo(
                    Url:           url,
                    Status:        hcStatus,
                    LastChecked:   lastChecked,
                    HealthCheckMs: hcMs,
                    TotalRequests: s?.TotalRequests  ?? 0,
                    ErrorCount:    s?.ErrorCount     ?? 0,
                    AvgResponseMs: s?.AvgResponseMs,
                    P95ResponseMs: s?.P95ResponseMs,
                    LastResponseMs:s?.LastResponseMs
                );
            }).ToArray();

            var svcStatus = instances.Length == 0
                ? WorkerStatus.Unknown
                : instances.All(i => i.Status == WorkerStatus.Healthy) ? WorkerStatus.Healthy
                : instances.Any(i => i.Status == WorkerStatus.Healthy)  ? WorkerStatus.Degraded
                : instances.Any(i => i.Status == WorkerStatus.Unknown)  ? WorkerStatus.Unknown
                : WorkerStatus.Unhealthy;

            return new WorkerServiceInfo(name, svcStatus, instances);
        }).ToArray();

    // ── Background polling ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var name in WorkerNames)
            {
                var urls = workerClient.GetUrls(name) ?? [];
                foreach (var url in urls)
                {
                    var key   = $"{name}|{url}";
                    var state = _states.GetOrAdd(key, _ => new InstanceState());
                    try
                    {
                        var sw      = System.Diagnostics.Stopwatch.StartNew();
                        var healthy = await workerClient.CheckHealthAsync(url, stoppingToken);
                        sw.Stop();
                        state.RecordHealthCheck(healthy, sw.Elapsed.TotalMilliseconds);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Health check failed for {Worker} @ {Url}", name, url);
                        state.RecordHealthCheck(false, null);
                    }
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Inner state ───────────────────────────────────────────────────────────

    private sealed class InstanceState
    {
        private readonly object _lock = new();
        // ring buffer of last 100 latency samples
        private readonly double[] _samples = new double[100];
        private int _head;
        private int _count;

        public WorkerStatus HealthStatus  { get; private set; } = WorkerStatus.Unknown;
        public double?      LastHealthCheckMs { get; private set; }
        public DateTime?    LastHealthChecked { get; private set; }
        public long         TotalRequests { get; private set; }
        public long         ErrorCount    { get; private set; }
        public double?      LastResponseMs { get; private set; }

        public double? AvgResponseMs
        {
            get
            {
                lock (_lock)
                {
                    if (_count == 0) return null;
                    var sum = 0.0;
                    for (var i = 0; i < _count; i++) sum += _samples[i];
                    return Math.Round(sum / _count, 1);
                }
            }
        }

        public double? P95ResponseMs
        {
            get
            {
                lock (_lock)
                {
                    if (_count < 2) return null;
                    var sorted = new double[_count];
                    for (var i = 0; i < _count; i++) sorted[i] = _samples[i];
                    Array.Sort(sorted);
                    var idx = (int)Math.Ceiling(0.95 * _count) - 1;
                    return Math.Round(sorted[Math.Max(0, idx)], 1);
                }
            }
        }

        public void RecordHealthCheck(bool healthy, double? ms)
        {
            lock (_lock)
            {
                HealthStatus      = healthy ? WorkerStatus.Healthy : WorkerStatus.Unhealthy;
                LastHealthCheckMs = ms.HasValue ? Math.Round(ms.Value, 1) : null;
                LastHealthChecked = DateTime.UtcNow;
            }
        }

        public void RecordRequest(double elapsedMs, bool success)
        {
            lock (_lock)
            {
                TotalRequests++;
                if (!success) ErrorCount++;
                LastResponseMs       = Math.Round(elapsedMs, 1);
                _samples[_head % 100] = elapsedMs;
                _head++;
                if (_count < 100) _count++;
            }
        }
    }
}
