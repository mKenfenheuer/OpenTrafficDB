using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>
/// Maintains the weekly speed profile of every road (<see cref="WayProfile"/>) and derives the
/// Valhalla predicted-speed encoding (<c>WaySpeedSummaries.EncodedSpeeds</c>) from it, which the
/// Valhalla CSV export and OpenTrafficRouting's predicted traffic use.
///
/// Samples are collected in memory per road and written every <c>Pipeline:ProfileFlushMinutes</c>
/// (default 5): one read and one write of a compact row per touched road, in key order and in
/// chunks. The prediction is computed in the same step, so no separate pass over the data is needed.
///
/// Prediction per five-minute slot (same rules as the original aggregate-based encoding):
/// the slot average blended with the free-flow speed for weeks without a sample, empty slots
/// filled from the nearest slot with data within ±60 min of the same day, free flow otherwise.
/// </summary>
public sealed class SpeedProfileWriter(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SpeedProfileWriter> logger) : BackgroundService
{
    private const int ChunkWays = 5000;
    private const int GapFillSlots = 12;   // ±60 min

    private readonly TimeSpan _flushInterval = TimeSpan.FromMinutes(
        double.TryParse(config["Pipeline:ProfileFlushMinutes"], out var f) ? f : 5);
    private readonly int _countCap = int.TryParse(config["Pipeline:ProfileCountCap"], out var c) ? Math.Clamp(c, 1, 255) : 52;

    private readonly object _lock = new();
    private Dictionary<long, List<WaySample>> _pending = [];

    /// <summary>Roads with samples waiting for the next profile flush.</summary>
    public int PendingWays { get { lock (_lock) return _pending.Count; } }

    public void Add(List<WaySample> samples)
    {
        if (samples.Count == 0) return;
        lock (_lock)
        {
            foreach (var s in samples)
            {
                if (!_pending.TryGetValue(s.WayId, out var list))
                    _pending[s.WayId] = list = new List<WaySample>(2);
                list.Add(s);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Speed profile writer started. FlushInterval={Interval}, CountCap={Cap}.",
            _flushInterval, _countCap);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await FlushAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Speed profile flush failed."); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(cancellationToken); }
        catch (Exception ex) { logger.LogWarning(ex, "Final profile flush on shutdown failed."); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        Dictionary<long, List<WaySample>> pending;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            pending = _pending;
            _pending = [];
        }

        var started = DateTime.UtcNow;
        var wayIds = pending.Keys.Order().ToArray();
        var failed = 0;

        for (int offset = 0; offset < wayIds.Length; offset += ChunkWays)
        {
            var chunk = wayIds.AsSpan(offset, Math.Min(ChunkWays, wayIds.Length - offset)).ToArray();
            try
            {
                await FlushChunkAsync(chunk, pending, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed += chunk.Length;
                logger.LogWarning(ex, "Profile flush of {Count} ways failed; samples re-queued.", chunk.Length);
                lock (_lock)
                    foreach (var id in chunk)
                    {
                        if (_pending.TryGetValue(id, out var newer)) newer.InsertRange(0, pending[id]);
                        else _pending[id] = pending[id];
                    }
            }
        }

        logger.LogInformation("Profiles: flushed {WayCount} ways in {ElapsedMs} ms ({Failed} re-queued).",
            wayIds.Length - failed, (long)(DateTime.UtcNow - started).TotalMilliseconds, failed);
    }

    private Task FlushChunkAsync(long[] chunk, Dictionary<long, List<WaySample>> pending, CancellationToken ct) =>
        ProcessChunkAsync(chunk, (wayId, profile, firstSeen) =>
        {
            var samples = pending[wayId];
            foreach (var s in samples.OrderBy(s => s.Timestamp))
                Apply(profile, Slot(s.Timestamp), s.SpeedMs * 3.6);
            var first = samples.Min(s => s.Timestamp);
            return (firstSeen is { } f && f < first ? f : first, samples.Max(s => s.Timestamp));
        }, ct);

    /// <summary>A road's history from the legacy WaySpeedAggregates table.</summary>
    public sealed record HistoryWay(long WayId, List<(int Slot, double AvgKmh, int Count)> Slots, DateTime FirstSeen, DateTime LastUpdated);

    /// <summary>
    /// Merges legacy history into the profiles (count-weighted average per slot, count capped) and
    /// re-encodes the predictions. Used once by <see cref="ProfileBackfillService"/>.
    /// </summary>
    public Task MergeHistoryAsync(List<HistoryWay> ways, CancellationToken ct)
    {
        var byId = ways.ToDictionary(w => w.WayId);
        return ProcessChunkAsync([.. byId.Keys.Order()], (wayId, profile, firstSeen) =>
        {
            var h = byId[wayId];
            foreach (var (slot, avgKmh, count) in h.Slots)
            {
                int curAvg = profile[slot * 2], curCount = profile[slot * 2 + 1];
                var total = curCount + count;
                if (total <= 0) continue;
                var avg = (curAvg * (double)curCount + avgKmh * count) / total;
                profile[slot * 2] = (byte)Math.Clamp(Math.Round(avg), 0, 255);
                profile[slot * 2 + 1] = (byte)Math.Min(total, _countCap);
            }
            var first = firstSeen is { } f && f < h.FirstSeen ? f : h.FirstSeen;
            return (first, h.LastUpdated);
        }, ct, keepNewerLastUpdated: true);
    }

    /// <summary>
    /// Locked read-modify-write of the profiles of one chunk of roads: reads the profiles with
    /// FOR UPDATE (so the live flush and the backfill never overwrite each other), applies
    /// <paramref name="mutate"/>, re-encodes the predictions and writes both back in one transaction.
    /// </summary>
    private async Task ProcessChunkAsync(long[] chunk,
        Func<long, byte[], DateTime?, (DateTime FirstSeen, DateTime LastUpdated)> mutate,
        CancellationToken ct, bool keepNewerLastUpdated = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = new Dictionary<long, (byte[] Profile, DateTime FirstSeen)>(chunk.Length);
        await using (var cmd = new NpgsqlCommand("""
            SELECT "OsmWayId", "Profile", "FirstSeenAt" FROM "WayProfiles"
            WHERE "OsmWayId" = ANY(@ids)
            ORDER BY "OsmWayId"
            FOR UPDATE
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = chunk });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing[reader.GetInt64(0)] = ((byte[])reader[1], reader.GetDateTime(2));
        }

        var freeflowById = new Dictionary<long, double>(chunk.Length);
        await using (var cmd = new NpgsqlCommand("""
            SELECT "OsmWayId", "FreeflowSpeed" FROM "WaySpeedSummaries" WHERE "OsmWayId" = ANY(@ids)
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = chunk });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                freeflowById[reader.GetInt64(0)] = reader.GetDouble(1);
        }

        var now = DateTime.UtcNow;
        var n = chunk.Length;
        var profiles = new byte[n][]; var firsts = new DateTime[n]; var lasts = new DateTime[n];
        var encoded = new string[n]; var freeflows = new double[n];

        for (int i = 0; i < n; i++)
        {
            var wayId = chunk[i];
            existing.TryGetValue(wayId, out var cur);
            var profile = cur.Profile is { Length: WayProfile.Slots * 2 } p ? p : new byte[WayProfile.Slots * 2];
            var (first, last) = mutate(wayId, profile, cur.Profile != null ? cur.FirstSeen : null);
            profiles[i] = profile;
            firsts[i] = first;
            lasts[i] = last;
            freeflows[i] = freeflowById.TryGetValue(wayId, out var ff) ? ff : MeanSpeed(profile);
        }

        // The DCT encoding is CPU-bound (~0.3 ms per road): run it on all cores.
        Parallel.For(0, n, () => new float[WayProfile.Slots], (i, _, buffer) =>
        {
            Predict(profiles[i], freeflows[i], firsts[i], now, buffer);
            encoded[i] = ValhallaSpeedEncoder.Encode(buffer);
            return buffer;
        }, _ => { });

        await using (var upsert = new NpgsqlCommand($"""
            INSERT INTO "WayProfiles" ("OsmWayId","Profile","FirstSeenAt","LastUpdated")
            SELECT * FROM unnest(@ids, @profiles, @firsts, @lasts)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "Profile"     = EXCLUDED."Profile",
                "FirstSeenAt" = EXCLUDED."FirstSeenAt",
                "LastUpdated" = {(keepNewerLastUpdated
                    ? "GREATEST(\"WayProfiles\".\"LastUpdated\", EXCLUDED.\"LastUpdated\")"
                    : "EXCLUDED.\"LastUpdated\"")}
            """, conn, tx))
        {
            upsert.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = chunk });
            upsert.Parameters.Add(new NpgsqlParameter("profiles", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = profiles });
            upsert.Parameters.Add(new NpgsqlParameter("firsts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = firsts });
            upsert.Parameters.Add(new NpgsqlParameter("lasts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = lasts });
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await using (var enc = new NpgsqlCommand("""
            UPDATE "WaySpeedSummaries" s SET "EncodedSpeeds" = v.enc
            FROM unnest(@ids, @enc) AS v(id, enc)
            WHERE s."OsmWayId" = v.id
            """, conn, tx))
        {
            enc.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = chunk });
            enc.Parameters.Add(new NpgsqlParameter("enc", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = encoded });
            await enc.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Monday-based five-minute slot of the week (same order as the Valhalla encoding).</summary>
    public static int Slot(DateTime t) => ((int)t.DayOfWeek + 6) % 7 * 288 + (t.Hour * 60 + t.Minute) / 5;

    /// <summary>Running average per slot; once the count reaches the cap it acts as a moving average.</summary>
    private void Apply(byte[] profile, int slot, double kmh)
    {
        int avg = profile[slot * 2], count = profile[slot * 2 + 1];
        var newCount = Math.Min(count + 1, _countCap);
        var newAvg = avg + (kmh - avg) / newCount;
        profile[slot * 2] = (byte)Math.Clamp(Math.Round(newAvg), 0, 255);
        profile[slot * 2 + 1] = (byte)newCount;
    }

    private static double MeanSpeed(byte[] profile)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < WayProfile.Slots; i++)
            if (profile[i * 2 + 1] > 0) { sum += profile[i * 2]; n++; }
        return n > 0 ? sum / n : 50;
    }

    /// <summary>Fills <paramref name="buffer"/> with the predicted speed (km/h) of every slot.</summary>
    public void Predict(byte[] profile, double freeflow, DateTime firstSeen, DateTime now, float[] buffer)
    {
        var weeks = Math.Clamp((int)Math.Ceiling((now - firstSeen).TotalDays / 7.0), 1, _countCap);
        var hasData = new bool[WayProfile.Slots];
        Array.Fill(buffer, (float)freeflow);

        for (int i = 0; i < WayProfile.Slots; i++)
        {
            int count = profile[i * 2 + 1];
            if (count == 0) continue;
            var missing = Math.Max(0, weeks - count);
            buffer[i] = (float)((profile[i * 2] * count + missing * freeflow) / (count + missing));
            hasData[i] = true;
        }

        for (int day = 0; day < 7; day++)
        {
            int dayBase = day * 288;
            for (int slot = 0; slot < 288; slot++)
            {
                if (hasData[dayBase + slot]) continue;
                for (int d = 1; d <= GapFillSlots; d++)
                {
                    int before = dayBase + ((slot - d) % 288 + 288) % 288;
                    if (hasData[before]) { buffer[dayBase + slot] = buffer[before]; break; }
                    int after = dayBase + (slot + d) % 288;
                    if (hasData[after]) { buffer[dayBase + slot] = buffer[after]; break; }
                }
            }
        }
    }
}
