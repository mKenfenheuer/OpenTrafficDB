using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Coordinator that partitions unmatched/unaggregated observations into time-window slots
/// and fans them out to all healthy AggregateWorker instances concurrently.
///
/// Config keys:
///   Coordinator:SlotMinutes     — width of each time slot (default: 60)
///   Coordinator:IntervalSeconds — how often the coordinator scans for work (default: 30)
/// </summary>
public class AggregationCoordinatorService(
    IServiceScopeFactory scopeFactory,
    WorkerClient workerClient,
    IConfiguration config,
    ILogger<AggregationCoordinatorService> logger) : BackgroundService
{
    private TimeSpan Interval  => TimeSpan.FromSeconds(double.Parse(config["Coordinator:IntervalSeconds"] ?? "30"));
    private TimeSpan SlotWidth => TimeSpan.FromMinutes(double.Parse(config["Coordinator:SlotMinutes"]     ?? "60"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Aggregation coordinator started. Interval: {Interval}, SlotWidth: {Slot}.",
            Interval, SlotWidth);

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try   { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Coordinator cycle failed."); }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var workerUrls = workerClient.GetUrls("Aggregate");
        if (workerUrls is null || workerUrls.Length == 0)
        {
            logger.LogWarning("No Aggregate worker URLs configured (Workers:Aggregate:Urls) — skipping cycle.");
            return;
        }

        // ── 1. Matching phase ─────────────────────────────────────────────────
        var matchSlots = await GetUnmatchedSlotsAsync(ct);
        logger.LogInformation("Cycle: {MatchSlots} match slots, workers: [{Workers}].",
            matchSlots.Count, string.Join(", ", workerUrls));
        if (matchSlots.Count > 0)
        {
            logger.LogInformation("Dispatching {Count} match slots to {Workers} workers.",
                matchSlots.Count, workerUrls.Length);
            await DispatchSlotsAsync(
                matchSlots,
                workerUrls,
                async (url, slot, token) =>
                {
                    var result = await workerClient.SendMatchJobAsync(
                        url, new MatchJobRequest(slot.From, slot.To), token);
                    if (result is not null)
                        logger.LogDebug("Match slot [{From} – {To}] on {Url}: {Matched} matched.",
                            slot.From, slot.To, url, result.Matched);
                    else
                        logger.LogWarning("Match slot [{From} – {To}] on {Url} failed.",
                            slot.From, slot.To, url);
                },
                ct);
        }

        // ── 2. Aggregation phase ──────────────────────────────────────────────
        var aggregateSlots = await GetUnaggregatedSlotsAsync(ct);
        logger.LogInformation("Cycle: {AggSlots} aggregate slots.", aggregateSlots.Count);
        if (aggregateSlots.Count > 0)
        {
            logger.LogInformation("Dispatching {Count} aggregate slots to {Workers} workers.",
                aggregateSlots.Count, workerUrls.Length);
            await DispatchSlotsAsync(
                aggregateSlots,
                workerUrls,
                async (url, slot, token) =>
                {
                    var result = await workerClient.SendAggregateJobAsync(
                        url, new AggregateJobRequest(slot.From, slot.To), token);
                    if (result is not null)
                        logger.LogDebug("Aggregate slot [{From} – {To}] on {Url}: {Processed} processed.",
                            slot.From, slot.To, url, result.Processed);
                    else
                        logger.LogWarning("Aggregate slot [{From} – {To}] on {Url} failed.",
                            slot.From, slot.To, url);
                },
                ct);

            // Finalize on the first worker after all aggregation slots complete
            await workerClient.SendFinalizeAsync(workerUrls[0], ct);
        }
    }

    /// <summary>
    /// Distribute slots across workers: each worker runs one slot at a time,
    /// pulling the next from the channel as soon as it finishes.
    /// </summary>
    private static async Task DispatchSlotsAsync(
        List<TimeSlot> slots,
        string[] workerUrls,
        Func<string, TimeSlot, CancellationToken, Task> send,
        CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<TimeSlot>();
        foreach (var slot in slots) channel.Writer.TryWrite(slot);
        channel.Writer.Complete();

        await Task.WhenAll(workerUrls.Select(url => Task.Run(async () =>
        {
            await foreach (var slot in channel.Reader.ReadAllAsync(ct))
                await send(url, slot, ct);
        }, ct)));
    }

    // ── Slot discovery ────────────────────────────────────────────────────────

    private async Task<List<TimeSlot>> GetUnmatchedSlotsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var range = await db.SpeedObservations
            .Where(o => o.OsmWayId == null && !o.IsAggregated)
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(o => o.Timestamp), Max = g.Max(o => o.Timestamp) })
            .FirstOrDefaultAsync(ct);

        return range is null ? [] : Partition(range.Min, range.Max, SlotWidth);
    }

    private async Task<List<TimeSlot>> GetUnaggregatedSlotsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var range = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && o.H3CellIndex != null)
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(o => o.Timestamp), Max = g.Max(o => o.Timestamp) })
            .FirstOrDefaultAsync(ct);

        return range is null ? [] : Partition(range.Min, range.Max, SlotWidth);
    }

    private static List<TimeSlot> Partition(DateTime min, DateTime max, TimeSpan slotWidth)
    {
        var slots  = new List<TimeSlot>();
        var cursor = min;
        var ceil   = max.Add(TimeSpan.FromSeconds(1)); // ensure the max timestamp is included
        while (cursor < ceil)
        {
            var end = cursor + slotWidth;
            slots.Add(new TimeSlot(cursor, end < ceil ? end : ceil));
            cursor = end;
        }
        return slots;
    }

    private record TimeSlot(DateTime From, DateTime To);
}
