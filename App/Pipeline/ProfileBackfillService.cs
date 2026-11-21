using System.Diagnostics;
using Npgsql;
using OpenTrafficDBData;
using Microsoft.EntityFrameworkCore;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>
/// One-time import of the legacy <c>WaySpeedAggregates</c> history (one row per road and
/// five-minute slot) into the weekly profiles (<c>WayProfiles</c>), started by an admin via
/// <c>POST /web/admin/profiles/backfill</c>.
///
/// The table is read in a single sequential scan sorted by road in Postgres (index scans are
/// disabled for this transaction: an index-ordered read of 200M rows would mean random heap
/// access). Rows are streamed, grouped per road and merged in batches through
/// <see cref="SpeedProfileWriter.MergeHistoryAsync"/>, which locks the profile rows so the live
/// profile flush and the import never overwrite each other. The legacy table is left unchanged.
/// </summary>
public sealed class ProfileBackfillService(
    IServiceScopeFactory scopeFactory,
    SpeedProfileWriter profiles,
    ILogger<ProfileBackfillService> logger) : BackgroundService
{
    private const int BatchWays = 5000;
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private int _running;

    public string State { get; private set; } = "idle";
    public long RowsRead { get; private set; }
    public long WaysMerged { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public string? Error { get; private set; }

    private DateTime? _reencodeSince;

    /// <summary>Starts the import unless it is already running. Returns false if it is.</summary>
    public bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _reencodeSince = null;
        State = "starting";
        _trigger.Release();
        return true;
    }

    /// <summary>
    /// Starts re-encoding the predictions of all roads whose summary changed since
    /// <paramref name="since"/> (e.g. after free-flow speeds were corrected). Shares the progress
    /// fields and the single-run guard with the import.
    /// </summary>
    public bool TryStartReencode(DateTime since)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _reencodeSince = since;
        State = "starting re-encode";
        _trigger.Release();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _trigger.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            StartedAt = DateTime.UtcNow; FinishedAt = null; Error = null; RowsRead = 0; WaysMerged = 0;
            try
            {
                if (_reencodeSince is { } since) await ReencodeAsync(since, stoppingToken);
                else await RunAsync(stoppingToken);
                State = "done";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { State = "cancelled"; }
            catch (Exception ex)
            {
                State = "failed";
                Error = ex.Message;
                logger.LogError(ex, "Profile backfill failed after {Rows} rows / {Ways} ways.", RowsRead, WaysMerged);
            }
            finally
            {
                FinishedAt = DateTime.UtcNow;
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        State = "reading";
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Profile backfill started: reading WaySpeedAggregates sorted by road.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.Zero);   // no timeout: the sort of the whole table takes minutes
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var set = new NpgsqlCommand("""
            SET LOCAL enable_indexscan = off;
            SET LOCAL enable_indexonlyscan = off;
            SET LOCAL enable_bitmapscan = off;
            SET LOCAL work_mem = '512MB';
            SET LOCAL max_parallel_workers_per_gather = 4;
            """, conn, tx))
            await set.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand("""
            SELECT "OsmWayId", "DayOfWeek", "FiveMinuteBucket", "SumSpeedKmh", "SampleCount", "FirstSeenAt", "LastUpdated"
            FROM "WaySpeedAggregates"
            WHERE "SampleCount" > 0
            ORDER BY "OsmWayId"
            """, conn, tx) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var batch = new List<SpeedProfileWriter.HistoryWay>(BatchWays);
        long currentWay = -1;
        List<(int Slot, double AvgKmh, int Count)>? slots = null;
        DateTime first = DateTime.MaxValue, last = DateTime.MinValue;

        while (await reader.ReadAsync(ct))
        {
            if (RowsRead == 0) { State = "merging"; logger.LogInformation("Profile backfill: sort done after {Elapsed}, merging.", stopwatch.Elapsed); }
            RowsRead++;

            var wayId = reader.GetInt64(0);
            if (wayId != currentWay)
            {
                if (slots != null) batch.Add(new(currentWay, slots, first, last));
                if (batch.Count >= BatchWays) await FlushAsync(batch, ct);
                currentWay = wayId; slots = []; first = DateTime.MaxValue; last = DateTime.MinValue;
            }

            int dow = reader.GetInt32(1), bucket = reader.GetInt32(2), count = reader.GetInt32(4);
            var slot = (dow + 6) % 7 * 288 + bucket;   // Monday-based, same order as the profiles
            if (slot is < 0 or >= OpenTrafficDBModels.WayProfile.Slots) continue;
            slots!.Add((slot, reader.GetDouble(3) / count, count));
            var f = reader.GetDateTime(5); var l = reader.GetDateTime(6);
            if (f < first) first = f;
            if (l > last) last = l;
        }
        if (slots != null) batch.Add(new(currentWay, slots, first, last));
        await FlushAsync(batch, ct);

        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        logger.LogInformation("Profile backfill done: {Rows} rows, {Ways} ways in {Elapsed}.", RowsRead, WaysMerged, stopwatch.Elapsed);
    }

    private async Task ReencodeAsync(DateTime since, CancellationToken ct)
    {
        State = "reading";
        var stopwatch = Stopwatch.StartNew();
        List<long> ids;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
            ids = await db.WaySpeedSummaries
                .Where(s => s.LastUpdated >= since && db.WayProfiles.Any(p => p.OsmWayId == s.OsmWayId))
                .OrderBy(s => s.OsmWayId)
                .Select(s => s.OsmWayId)
                .ToListAsync(ct);
        }
        RowsRead = ids.Count;
        State = "re-encoding";
        logger.LogInformation("Profile re-encode: {Count} roads changed since {Since:u}.", ids.Count, since);

        for (int i = 0; i < ids.Count; i += BatchWays)
        {
            var chunk = ids.GetRange(i, Math.Min(BatchWays, ids.Count - i)).ToArray();
            await profiles.ReencodeAsync(chunk, ct);
            WaysMerged += chunk.Length;
            if (WaysMerged % 100_000 < BatchWays)
                logger.LogInformation("Profile re-encode: {Done}/{Total} roads.", WaysMerged, ids.Count);
        }
        logger.LogInformation("Profile re-encode done: {Count} roads in {Elapsed}.", ids.Count, stopwatch.Elapsed);
    }

    private async Task FlushAsync(List<SpeedProfileWriter.HistoryWay> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await profiles.MergeHistoryAsync(batch, ct);
        WaysMerged += batch.Count;
        if (WaysMerged % 100_000 < BatchWays)
            logger.LogInformation("Profile backfill: {Ways} ways merged ({Rows} rows read).", WaysMerged, RowsRead);
        batch.Clear();
    }
}
