namespace OpenTrafficDB.App.Services;

public enum WorkerStatus { Unknown, Healthy, Unhealthy }

public record WorkerInfo(string Name, WorkerStatus Status, DateTime? LastChecked, string[] Urls);

/// <summary>
/// Background service that polls all workers every 10 seconds and caches their health status.
/// The cached status is served to the admin panel without blocking.
/// </summary>
public class WorkerHealthService(
    WorkerClient workerClient,
    ILogger<WorkerHealthService> logger) : BackgroundService
{
    private static readonly string[] WorkerNames = ["Ingest", "Aggregate", "Routing"];
    private readonly Dictionary<string, (WorkerStatus Status, DateTime? LastChecked)> _statuses
        = WorkerNames.ToDictionary(n => n, _ => (WorkerStatus.Unknown, (DateTime?)null));

    public WorkerInfo[] GetAllStatuses() => WorkerNames
        .Select(n =>
        {
            var (status, lastChecked) = _statuses[n];
            return new WorkerInfo(n, status, lastChecked, workerClient.GetUrls(n) ?? []);
        })
        .ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var name in WorkerNames)
            {
                try
                {
                    var healthy = await workerClient.CheckHealthAsync(name, stoppingToken);
                    _statuses[name] = (healthy ? WorkerStatus.Healthy : WorkerStatus.Unhealthy, DateTime.UtcNow);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Health check failed for worker {Worker}", name);
                    _statuses[name] = (WorkerStatus.Unhealthy, DateTime.UtcNow);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
