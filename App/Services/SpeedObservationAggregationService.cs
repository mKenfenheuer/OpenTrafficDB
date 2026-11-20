using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDBData;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Continuous aggregation worker.
///
/// Aggregates matched observations (<c>OsmWayId IS NOT NULL AND NOT IsAggregated</c>) into
/// the weekly speed profiles and live summaries. A pass runs as soon as matched
/// observations are waiting; otherwise the worker polls every
/// <c>Aggregation:IdlePollSeconds</c> (default 2 s). Small, frequent passes keep the
/// processing rate steady instead of one large batch every interval.
///
/// Stale-row cleanup (<see cref="TrafficAggregationService.FinalizeAsync"/>) runs on its own
/// timer every <c>Aggregation:FinalizeIntervalSeconds</c> (default 60 s).
/// </summary>
public class SpeedObservationAggregationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SpeedObservationAggregationService> logger) : BackgroundService
{
    private TimeSpan IdlePoll         => TimeSpan.FromSeconds(double.Parse(config["Aggregation:IdlePollSeconds"] ?? "2"));
    private TimeSpan FinalizeInterval => TimeSpan.FromSeconds(double.Parse(config["Aggregation:FinalizeIntervalSeconds"] ?? "60"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Aggregation service started. IdlePoll={IdlePoll}, FinalizeInterval={FinalizeInterval}.",
            IdlePoll, FinalizeInterval);

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var lastFinalize = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var aggregated = 0;
            try
            {
                aggregated = await ProcessOncePassAsync(stoppingToken);

                if (DateTime.UtcNow - lastFinalize >= FinalizeInterval)
                {
                    await FinalizeAsync(stoppingToken);
                    lastFinalize = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Aggregation pass failed.");
            }

            if (aggregated == 0)
                await Task.Delay(IdlePoll, stoppingToken);
        }
    }

    /// <summary>Aggregates all currently matched observations. Returns the number processed.</summary>
    private async Task<int> ProcessOncePassAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = scopeFactory.CreateScope();
        var db          = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var aggregation = scope.ServiceProvider.GetRequiredService<TrafficAggregationService>();

        var pending = await db.SpeedObservations
            .AnyAsync(o => !o.IsAggregated && o.OsmWayId != null, ct);
        if (!pending) return 0;

        var result = await aggregation.ReaggregateAsync(ct);
        var aggregated = result.Processed;
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var msPerObs = aggregated > 0 ? stopwatch.ElapsedMilliseconds / (double)aggregated : 0;
            logger.LogDebug("Aggregated {Count} observations in {ElapsedMs}ms, {MsPerObs}ms per observation.",
                aggregated, stopwatch.ElapsedMilliseconds, msPerObs);
        }
        return aggregated;
    }

    private async Task FinalizeAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var aggregation = scope.ServiceProvider.GetRequiredService<TrafficAggregationService>();
        await aggregation.FinalizeAsync(ct);
    }
}
