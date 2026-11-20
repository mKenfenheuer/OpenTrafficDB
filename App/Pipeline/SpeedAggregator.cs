using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.App.Services;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Pipeline;

/// <summary>One traversal of a road by a trip: average speed and time on that road.</summary>
public readonly record struct WaySample(long WayId, DateTime Timestamp, double SpeedMs, double? SpeedLimitKmh);

/// <summary>
/// Collects <see cref="WaySample"/>s in memory and writes them to Postgres every
/// <c>Pipeline:FlushSeconds</c> (default 10 s) as two bulk upserts:
/// <list type="bullet">
/// <item><c>WaySpeedAggregates</c>: per road, weekday and 5-minute bucket, sum and count of speeds.</item>
/// <item><c>WaySpeedSummaries</c>: live (constrained) and free-flow speed per road as an
/// exponential moving average, plus the encoded Valhalla speed profile.</item>
/// </list>
/// Raw observations are never stored. Pending samples are flushed on graceful shutdown.
/// </summary>
public sealed class SpeedAggregator(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SpeedAggregator> logger) : BackgroundService
{
    private const double EmaAlpha = 0.8;

    private readonly object _lock = new();
    private List<WaySample> _pending = [];
    private long _flushedTotal;
    private long _lastSummariesMs;
    private const int UpsertChunkRows = 20_000;

    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(
        double.TryParse(config["Pipeline:FlushSeconds"], out var f) ? f : 10);
    private readonly TimeSpan _finalizeInterval = TimeSpan.FromSeconds(
        double.TryParse(config["Aggregation:FinalizeIntervalSeconds"], out var fi) ? fi : 60);

    /// <summary>Samples waiting for the next flush.</summary>
    public int PendingSamples { get { lock (_lock) return _pending.Count; } }

    /// <summary>Cumulative number of samples written to the database.</summary>
    public long FlushedTotal => Interlocked.Read(ref _flushedTotal);

    public void Add(List<WaySample> samples)
    {
        if (samples.Count == 0) return;
        lock (_lock) _pending.AddRange(samples);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Speed aggregator started. FlushInterval={Interval}.", _flushInterval);
        var lastFinalize = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await FlushAsync(stoppingToken);

                if (DateTime.UtcNow - lastFinalize >= _finalizeInterval)
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<TrafficAggregationService>().FinalizeAsync(stoppingToken);
                    lastFinalize = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Speed aggregation flush failed.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(cancellationToken); }
        catch (Exception ex) { logger.LogWarning(ex, "Final flush on shutdown failed."); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        List<WaySample> samples;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            samples = _pending;
            _pending = new List<WaySample>(samples.Count);
        }

        var stopwatch = Stopwatch.StartNew();
        // Sorted by way id: upserts then walk the unique indexes in key order, which turns random
        // I/O on the large aggregate table into mostly sequential page access.
        var byWay = samples.GroupBy(s => s.WayId)
            .OrderBy(g => g.Key)
            .Select(g => (WayId: g.Key, Samples: g.OrderBy(s => s.Timestamp).ToList()))
            .ToList();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // WaySpeedAggregates is large (one row per road and 5-minute slot of the week); a flush can
        // take longer than the default 30 s command timeout under load.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));

        try
        {
            // One transaction: a failed flush is rolled back completely before its samples are
            // re-queued, so no sample is counted twice.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Live summaries first: they drive the near-live map and the live CSV.
            var summariesMs = stopwatch.ElapsedMilliseconds;
            await UpsertSummariesAsync(db, byWay, ct);
            summariesMs = stopwatch.ElapsedMilliseconds - summariesMs;
            await UpsertAggregatesAsync(db, byWay, ct);
            _lastSummariesMs = summariesMs;

            db.AggregationMetrics.Add(new AggregationMetric
            {
                ProcessedAt = DateTime.UtcNow,
                ObservationCount = samples.Count,
                WayCount = byWay.Count,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Put the samples back so the next flush retries them.
            lock (_lock) _pending.InsertRange(0, samples);
            throw;
        }

        Interlocked.Add(ref _flushedTotal, samples.Count);
        logger.LogInformation("Flushed {SampleCount} samples for {WayCount} ways in {ElapsedMs} ms (summaries {SummariesMs} ms).",
            samples.Count, byWay.Count, stopwatch.ElapsedMilliseconds, _lastSummariesMs);
    }

    private static int BucketIndex(DateTime t) =>
        ((int)t.DayOfWeek + 6) % 7 * 288 + (t.Hour * 60 + t.Minute) / 5;

    private readonly record struct AggregateRow(
        long WayId, int Dow, int Bucket, double Sum, int Count, double? Limit, DateTime First, DateTime Last);

    private static async Task UpsertAggregatesAsync(AppDbContext db,
        List<(long WayId, List<WaySample> Samples)> byWay, CancellationToken ct)
    {
        var rows = new List<AggregateRow>();
        foreach (var (wayId, list) in byWay)
            foreach (var g in list.GroupBy(s => BucketIndex(s.Timestamp)))
                rows.Add(new AggregateRow(wayId, (g.Key / 288 + 1) % 7, g.Key % 288,
                    g.Sum(s => s.SpeedMs * 3.6), g.Count(),
                    g.LastOrDefault(s => s.SpeedLimitKmh != null).SpeedLimitKmh,
                    g.Min(s => s.Timestamp), g.Max(s => s.Timestamp)));

        // Key order of the unique index (OsmWayId, DayOfWeek, FiveMinuteBucket).
        rows.Sort((x, y) =>
        {
            var c = x.WayId.CompareTo(y.WayId);
            if (c != 0) return c;
            c = x.Dow.CompareTo(y.Dow);
            return c != 0 ? c : x.Bucket.CompareTo(y.Bucket);
        });

        for (int offset = 0; offset < rows.Count; offset += UpsertChunkRows)
        {
            var chunk = rows.Skip(offset).Take(UpsertChunkRows).ToArray();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "WaySpeedAggregates"
                ("OsmWayId","DayOfWeek","FiveMinuteBucket",
                 "SumSpeedKmh","SampleCount","SpeedLimitKmh","LastUpdated","FirstSeenAt")
                SELECT * FROM unnest(@way_ids,@dows,@buckets,@sums,@counts,@limits,@last,@first)
                ON CONFLICT ("OsmWayId","DayOfWeek","FiveMinuteBucket") DO UPDATE SET
                    "SumSpeedKmh"   = "WaySpeedAggregates"."SumSpeedKmh" + EXCLUDED."SumSpeedKmh",
                    "SampleCount"   = "WaySpeedAggregates"."SampleCount" + EXCLUDED."SampleCount",
                    "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh","WaySpeedAggregates"."SpeedLimitKmh"),
                    "LastUpdated"   = EXCLUDED."LastUpdated"
                """,
                [
                    new NpgsqlParameter("way_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = chunk.Select(r => r.WayId).ToArray() },
                    new NpgsqlParameter("dows", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => r.Dow).ToArray() },
                    new NpgsqlParameter("buckets", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => r.Bucket).ToArray() },
                    new NpgsqlParameter("sums", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = chunk.Select(r => r.Sum).ToArray() },
                    new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => r.Count).ToArray() },
                    new NpgsqlParameter("limits", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = chunk.Select(r => r.Limit).ToArray() },
                    new NpgsqlParameter("last", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = chunk.Select(r => r.Last).ToArray() },
                    new NpgsqlParameter("first", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = chunk.Select(r => r.First).ToArray() },
                ],
                ct);
        }
    }

    /// <summary>
    /// Updates WaySpeedSummaries with an exponential moving average (α = 0.8), oldest sample first,
    /// so the most recent traversal has the highest weight. Same math as the former
    /// database-queue aggregation, applied to samples instead of raw points.
    /// </summary>
    private static async Task UpsertSummariesAsync(AppDbContext db,
        List<(long WayId, List<WaySample> Samples)> byWay, CancellationToken ct)
    {
        var ids = byWay.Select(w => w.WayId).ToArray();
        var seeds = await db.WaySpeedSummaries
            .Where(s => ids.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var n = byWay.Count;
        var wayIds = new long[n]; var constrained = new double[n]; var freeflow = new double[n];
        var speedLimits = new double?[n]; var lastUpdated = new DateTime[n]; var encoded = new string[n];
        var buffer = new float[2016];

        for (int i = 0; i < n; i++)
        {
            var (wayId, list) = byWay[i];
            seeds.TryGetValue(wayId, out var seed);

            double? speedLimit = null;
            foreach (var s in list)
                if (s.SpeedMs != 0) { speedLimit = s.SpeedMs * 3.6; break; }

            double emaC = seed?.ConstrainedSpeed ?? seed?.FreeflowSpeed ?? list[0].SpeedMs * 3.6;
            foreach (var s in list)
                emaC = emaC * (1 - EmaAlpha) + s.SpeedMs * 3.6 * EmaAlpha;

            double emaF = seed?.FreeflowSpeed ?? emaC;
            foreach (var s in list)
            {
                if (speedLimit is > 0 && s.SpeedMs * 3.6 < speedLimit * 0.8) continue;
                emaF = emaF * (1 - EmaAlpha) + s.SpeedMs * 3.6 * EmaAlpha;
            }

            if (speedLimit is > 0)
            {
                emaF = Math.Max(emaF, speedLimit.Value * 0.6);
                emaF = Math.Min(emaF, speedLimit.Value);
            }

            wayIds[i] = wayId;
            constrained[i] = Math.Round(emaC, 1);
            freeflow[i] = Math.Round(emaF, 1);
            speedLimits[i] = speedLimit;
            lastUpdated[i] = list[^1].Timestamp;
            Array.Fill(buffer, (float)freeflow[i]);
            encoded[i] = ValhallaSpeedEncoder.Encode(buffer);
        }

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "WaySpeedSummaries"
            ("OsmWayId","ConstrainedSpeed","FreeflowSpeed","SpeedLimitKmh","LastUpdated","EncodedSpeeds")
            SELECT * FROM unnest(@way_ids,@c,@f,@sl,@lu,@enc)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "ConstrainedSpeed" = EXCLUDED."ConstrainedSpeed",
                "FreeflowSpeed"    = EXCLUDED."FreeflowSpeed",
                "SpeedLimitKmh"    = COALESCE(EXCLUDED."SpeedLimitKmh","WaySpeedSummaries"."SpeedLimitKmh"),
                "LastUpdated"      = EXCLUDED."LastUpdated",
                "EncodedSpeeds"    = EXCLUDED."EncodedSpeeds"
            """,
            [
                new NpgsqlParameter("way_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = wayIds },
                new NpgsqlParameter("c", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = constrained },
                new NpgsqlParameter("f", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = freeflow },
                new NpgsqlParameter("sl", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = speedLimits },
                new NpgsqlParameter("lu", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = lastUpdated },
                new NpgsqlParameter("enc", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = encoded },
            ],
            ct);
    }
}
