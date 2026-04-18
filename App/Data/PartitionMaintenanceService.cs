using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenTrafficDBData;

/// <summary>
/// Ensures monthly SpeedObservations partitions exist for the current month
/// plus <see cref="MonthsAhead"/> future months. Runs once at startup and
/// then every 24 hours so new months are always pre-created.
/// </summary>
public class PartitionMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<PartitionMaintenanceService> logger) : BackgroundService
{
    private const int MonthsAhead = 3;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Run immediately on startup, then once per day.
        while (!ct.IsCancellationRequested)
        {
            await EnsurePartitionsAsync(ct);
            await Task.Delay(TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        }
    }

    private async Task EnsurePartitionsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        for (var i = 0; i <= MonthsAhead; i++)
        {
            var month = now.AddMonths(i);
            var from  = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var to    = from.AddMonths(1);
            var name  = $"SpeedObservations_{from:yyyy_MM}";

            try
            {
                await db.Database.ExecuteSqlRawAsync($"""
                    CREATE TABLE IF NOT EXISTS "{name}"
                        PARTITION OF "SpeedObservations"
                        FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}')
                        WITH (autovacuum_vacuum_scale_factor  = 0.01,
                              autovacuum_analyze_scale_factor = 0.005,
                              autovacuum_vacuum_cost_delay    = 2)
                    """, ct);

                logger.LogDebug("Partition {Name} ensured", name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ensure partition {Name}", name);
            }
        }
    }
}
