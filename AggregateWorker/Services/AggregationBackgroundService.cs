using OpenTrafficDB.Shared.Data;

namespace OpenTrafficDB.AggregateWorker.Services;

/// <summary>
/// Background service that runs every 5 minutes:
/// 1. Map-matches unmatched GPS observations to OSM way IDs.
/// 2. Re-aggregates all matched observations into RoadSpeedAggregates.
/// </summary>
public class AggregationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AggregationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Aggregation background service started. Interval: {Interval}.", _interval);

        // Initial delay to let the app fully start
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in aggregation background service cycle.");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        // Each step gets its own scope+DbContext so that ExecuteDeleteAsync in
        // ReaggregateAsync cannot affect rows still tracked by the matching step.
        logger.LogDebug("Running map-matching pass...");
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var matchingService = scope.ServiceProvider.GetRequiredService<ValhallaMapMatchingService>();
            await matchingService.ProcessUnmatchedAsync(db, ct);
        }

        logger.LogDebug("Running aggregation pass...");
        using (var scope = scopeFactory.CreateScope())
        {
            var aggregationService = scope.ServiceProvider.GetRequiredService<TrafficAggregationService>();
            await aggregationService.ReaggregateAsync(ct);
        }
    }
}
