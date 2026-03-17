using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;

namespace OpenTrafficDB.Shared.Services;

/// <summary>
/// Periodically scans for OSM way IDs that have no stored geometry and fetches
/// their shapes from the Overpass API in batches.
/// </summary>
public class WayGeometryBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WayGeometryBackgroundService> logger) : BackgroundService
{
    // How often to check for missing geometries
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    // Ways per Overpass request
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay so the DB is definitely ready
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnrichAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WayGeometryBackgroundService encountered an error");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task EnrichAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<WayGeometryService>();

        // Keep fetching batches until nothing is left to enrich in this cycle
        int total = 0;
        int fetched;
        do
        {
            fetched = await svc.EnrichMissingAsync(db, BatchSize, ct);
            total += fetched;
        } while (fetched == BatchSize && !ct.IsCancellationRequested);

        if (total > 0)
            logger.LogInformation("WayGeometry enrichment cycle complete: {Total} ways fetched", total);
    }
}
