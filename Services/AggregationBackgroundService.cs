using OpenTrafficDB.Data;

namespace OpenTrafficDB.Services;

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
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matchingService = scope.ServiceProvider.GetRequiredService<ValhallaMapMatchingService>();
        var aggregationService = scope.ServiceProvider.GetRequiredService<TrafficAggregationService>();

        logger.LogDebug("Running map-matching pass...");
        await matchingService.ProcessUnmatchedAsync(db, ct);

        logger.LogDebug("Running aggregation pass...");
        await aggregationService.ReaggregateAsync(ct);
    }
}
