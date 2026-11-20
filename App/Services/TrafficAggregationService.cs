using System.Buffers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;
using OsmSharp;

namespace OpenTrafficDB.App.Services;

public class TrafficAggregationService(
    AppDbContext db,
    ReadOnlyAppDbContext readDb,
    IConfiguration config,
    ILogger<TrafficAggregationService> logger)
{
    private readonly TimeSpan _liveWindow = TimeSpan.FromMinutes(
        double.TryParse(config["Traffic:LiveWindowMinutes"], out var lw) ? lw : 60);

    /// <summary>
    /// Periodic cleanup: prunes old AggregationMetrics rows and removes leftover rows of the former
    /// database queue. The speed pipeline keeps raw observations in memory and never writes
    /// SpeedObservations, so any row there is legacy data.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        var legacyCutoff = DateTime.UtcNow.AddMinutes(-10);
        var deleted = await db.SpeedObservations
            .Where(o => o.CreatedAt < legacyCutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            logger.LogInformation("Finalize: deleted {Count} legacy speed observations", deleted);

        // Processed hazard observations are only raw input; the state lives in HazardAggregates.
        var hazardCutoff = DateTime.UtcNow.AddDays(-1);
        var hazardsDeleted = await db.HazardObservations
            .Where(o => o.IsProcessed && o.CreatedAt < hazardCutoff)
            .ExecuteDeleteAsync(ct);
        if (hazardsDeleted > 0)
            logger.LogInformation("Finalize: deleted {Count} processed hazard observations older than 1 day", hazardsDeleted);

        // Prune metric rows older than 24 h — they're only needed for the rolling rate window.
        var metricCutoff = DateTime.UtcNow.AddHours(-24);
        await db.AggregationMetrics
            .Where(m => m.ProcessedAt < metricCutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static double BlendedAvg(double sum, int count, DateTime first, double freeflow, DateTime now)
    {
        var totalSlots = Math.Max(1, (int)Math.Ceiling((now - first).TotalDays / 7.0));
        var missing = Math.Max(0, totalSlots - count);
        return (sum + missing * freeflow) / (count + missing);
    }


    // ── Valhalla CSV export ─────────────────────────────────────────────────────

    /// <summary>
    /// Streams the Valhalla predicted-traffic CSV from pre-computed encoded speeds in WaySpeedSummaries.
    /// No DCT computation at export time — encoding happens during aggregation.
    /// </summary>
    public async Task WriteValhallaExportCsvAsync(Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 65536, leaveOpen: true);
        await writer.WriteLineAsync("way_id,freeflow_speed,constrained_speed,historical_speeds");

        await foreach (var s in readDb.WaySpeedSummaries
            .Where(s => s.EncodedSpeeds != null)
            .OrderBy(s => s.OsmWayId)
            .AsNoTracking()
            .Select(s => new { s.OsmWayId, s.FreeflowSpeed, s.ConstrainedSpeed, s.EncodedSpeeds })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            var freeflow = (int)Math.Round(s.FreeflowSpeed);
            var constrained = (int)Math.Round(s.ConstrainedSpeed ?? s.FreeflowSpeed);
            await writer.WriteLineAsync($"{s.OsmWayId},{freeflow},{constrained},{s.EncodedSpeeds}");
        }
    }

    // ── Live traffic CSV export ───────────────────────────────────────────────────

    /// <summary>
    /// Streams a simple way_id,speed_kmh CSV of current live speeds from WaySpeedSummaries.
    /// Intended for external tools that build Valhalla traffic.tar files.
    /// Only includes ways updated within the live window (Traffic:LiveWindowMinutes, default 60).
    /// </summary>
    public async Task WriteLiveTrafficCsvAsync(Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 65536, leaveOpen: true);
        await writer.WriteLineAsync("way_id,speed_kmh,freeflow_kmh,updated_unix");

        var cutoff = DateTime.UtcNow - _liveWindow;

        await foreach (var s in readDb.WaySpeedSummaries
            .Where(s => s.LastUpdated >= cutoff)
            .OrderBy(s => s.OsmWayId)
            .AsNoTracking()
            .Select(s => new { s.OsmWayId, Speed = s.ConstrainedSpeed ?? s.FreeflowSpeed, s.FreeflowSpeed, s.LastUpdated })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            // Measurement time, so the router can tell live from stale data (it used the sync time).
            await writer.WriteLineAsync(FormattableString.Invariant(
                $"{s.OsmWayId},{s.Speed:F1},{s.FreeflowSpeed:F1},{new DateTimeOffset(DateTime.SpecifyKind(s.LastUpdated, DateTimeKind.Utc)).ToUnixTimeSeconds()}"));
        }
    }

}
