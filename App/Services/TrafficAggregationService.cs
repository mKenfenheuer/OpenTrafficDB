using System.Buffers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Services;

public class TrafficAggregationService(
    AppDbContext db,
    ReadOnlyAppDbContext readDb,
    ILogger<TrafficAggregationService> logger)
{
    private const double EmaAlpha = 0.8;
    private static readonly TimeSpan UnmatchedRetention = TimeSpan.FromHours(24);

    private struct Obs
    {
        public long WayId;
        public DateTime Timestamp;
        public double SpeedKmh;
        public double? SpeedLimit;
    }

    public async Task<AggregateJobResult> ReaggregateRangeAsync(
        long[] wayIds, CancellationToken ct = default)
    {
        if (wayIds.Length == 0) return new AggregateJobResult(0);

        logger.LogDebug("Aggregating {WayCount} ways", wayIds.Length);

        var observations = await db.SpeedObservations
            .AsNoTracking()
            .Where(o => !o.IsAggregated &&
                        o.OsmWayId != null &&
                        wayIds.Contains(o.OsmWayId.Value))
            .Select(o => new Obs
            {
                WayId = o.OsmWayId!.Value,
                Timestamp = o.Timestamp,
                SpeedKmh = o.SpeedMs * 3.6,
                SpeedLimit = o.SpeedLimitKmh
            })
            .ToListAsync(ct);

        if (observations.Count == 0)
            return new AggregateJobResult(0);

        // Partition by way (no GroupBy)
        var perWay = new Dictionary<long, List<Obs>>(wayIds.Length);
        foreach (var o in observations)
        {
            if (!perWay.TryGetValue(o.WayId, out var list))
            {
                list = new List<Obs>(32);
                perWay[o.WayId] = list;
            }
            list.Add(o);
        }

        var wayEntries = perWay.ToArray();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        var threadLocal = new List<WaySpeedAggregate>[Environment.ProcessorCount];

        Parallel.For(0, wayEntries.Length, options, i =>
        {
            var (wayId, list) = wayEntries[i];

            var local = threadLocal[Thread.GetCurrentProcessorId() % threadLocal.Length]
                        ??= new List<WaySpeedAggregate>(64);

            var sums = new double[2016];
            var counts = new int[2016];
            var first = new DateTime[2016];
            var last = new DateTime[2016];
            var limits = new double?[2016];
            var used = new bool[2016];

            foreach (var o in list)
            {
                int idx = ((int)o.Timestamp.DayOfWeek + 6) % 7 * 288
                        + (o.Timestamp.Hour * 60 + o.Timestamp.Minute) / 5;

                sums[idx] += o.SpeedKmh;
                counts[idx]++;

                if (!used[idx])
                {
                    first[idx] = o.Timestamp;
                    last[idx] = o.Timestamp;
                    used[idx] = true;
                }
                else
                {
                    if (o.Timestamp < first[idx]) first[idx] = o.Timestamp;
                    if (o.Timestamp > last[idx]) last[idx] = o.Timestamp;
                }

                if (o.SpeedLimit != null)
                    limits[idx] = o.SpeedLimit;
            }

            for (int idx = 0; idx < 2016; idx++)
            {
                if (!used[idx]) continue;

                local.Add(new WaySpeedAggregate
                {
                    OsmWayId = wayId,
                    DayOfWeek = (idx / 288 + 1) % 7,
                    FiveMinuteBucket = idx % 288,
                    SumSpeedKmh = sums[idx],
                    SampleCount = counts[idx],
                    SpeedLimitKmh = limits[idx],
                    FirstSeenAt = first[idx],
                    LastUpdated = last[idx]
                });
            }
        });

        var aggregates = threadLocal
            .Where(x => x != null)
            .SelectMany(x => x)
            .ToArray();

        await BulkUpsertAggregatesAsync(aggregates, ct);

        await UpdateSummariesEmaAsync(wayIds, ct);

        var processed = await db.SpeedObservations
            .Where(o => !o.IsAggregated &&
                        o.OsmWayId != null &&
                        wayIds.Contains(o.OsmWayId.Value))
            .ExecuteDeleteAsync(ct);

        if (processed > 0)
        {
            db.AggregationMetrics.Add(new AggregationMetric
            {
                ProcessedAt = DateTime.UtcNow,
                ObservationCount = processed,
                WayCount = wayIds.Length,
            });
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Aggregated {WayCount} ways, deleted {ObsCount} observations", wayIds.Length, processed);
        return new AggregateJobResult(processed);
    }

    private async Task BulkUpsertAggregatesAsync(WaySpeedAggregate[] aggs, CancellationToken ct)
    {
        if (aggs.Length == 0) return;

        var wayIds = new long[aggs.Length];
        var dows = new int[aggs.Length];
        var buckets = new int[aggs.Length];
        var sums = new double[aggs.Length];
        var counts = new int[aggs.Length];
        var limits = new double?[aggs.Length];
        var last = new DateTime[aggs.Length];
        var first = new DateTime[aggs.Length];

        for (int i = 0; i < aggs.Length; i++)
        {
            var a = aggs[i];
            wayIds[i] = a.OsmWayId;
            dows[i] = a.DayOfWeek;
            buckets[i] = a.FiveMinuteBucket;
            sums[i] = a.SumSpeedKmh;
            counts[i] = a.SampleCount;
            limits[i] = a.SpeedLimitKmh;
            last[i] = a.LastUpdated;
            first[i] = a.FirstSeenAt;
        }

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "WaySpeedAggregates"
            ("OsmWayId","DayOfWeek","FiveMinuteBucket",
             "SumSpeedKmh","SampleCount","SpeedLimitKmh","LastUpdated","FirstSeenAt")
            SELECT * FROM unnest(
                @way_ids,@dows,@buckets,@sums,@counts,@limits,@last,@first)
            ON CONFLICT ("OsmWayId","DayOfWeek","FiveMinuteBucket") DO UPDATE SET
                "SumSpeedKmh" = "WaySpeedAggregates"."SumSpeedKmh" + EXCLUDED."SumSpeedKmh",
                "SampleCount" = "WaySpeedAggregates"."SampleCount" + EXCLUDED."SampleCount",
                "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh","WaySpeedAggregates"."SpeedLimitKmh"),
                "LastUpdated" = EXCLUDED."LastUpdated"
            """,
            [
                new NpgsqlParameter("way_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = wayIds },
                new NpgsqlParameter("dows", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = dows },
                new NpgsqlParameter("buckets", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = buckets },
                new NpgsqlParameter("sums", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = sums },
                new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = counts },
                new NpgsqlParameter("limits", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = limits },
                new NpgsqlParameter("last", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = last },
                new NpgsqlParameter("first", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = first }
            ],
            ct);
    }

    /// <summary>
    /// Runs global cleanup after all aggregation slots complete:
    /// refreshes constrained speeds and deletes observations outside retention windows.
    /// </summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        // Delete stale unmatched observations:
        // - IsAggregated=true && OsmWayId==null: explicitly given up on by the matching pipeline — delete immediately.
        // - OsmWayId==null && old: never processed (shouldn't happen in normal operation) — delete after retention window.
        // Aggregated observations with OsmWayId set are deleted eagerly in ReaggregateRangeAsync.
        var unmatchedCutoff = DateTime.UtcNow - UnmatchedRetention;

        var deleted = await db.SpeedObservations
            .Where(o => o.OsmWayId == null && (o.IsAggregated || o.CreatedAt < unmatchedCutoff))
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            logger.LogInformation("Finalize: deleted {Count} stale unmatched observations", deleted);
        else
            logger.LogDebug("Finalize: no stale unmatched observations to delete");

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


    /// <summary>
    /// Updates WaySpeedSummaries for the given ways using an exponential moving average (α=0.8).
    /// Observations are applied oldest-first so the most recent reading has the highest weight.
    /// All summary fields (ConstrainedSpeed, FreeflowSpeed) are updated
    /// with the same rolling pattern: new = old * 0.2 + batch_value * 0.8.
    /// Must be called before the observations are deleted.
    /// </summary>
    private async Task UpdateSummariesEmaAsync(long[] wayIds, CancellationToken ct)
    {
        if (wayIds.Length == 0) return;

        var now = DateTime.UtcNow;

        var seeds = await db.WaySpeedSummaries
            .Where(s => wayIds.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        var obs = await db.SpeedObservations
            .AsNoTracking()
            .Where(o => !o.IsAggregated &&
                        o.OsmWayId != null &&
                        wayIds.Contains(o.OsmWayId.Value))
            .Select(o => new Obs
            {
                WayId = o.OsmWayId!.Value,
                Timestamp = o.Timestamp,
                SpeedKmh = o.SpeedMs * 3.6,
                SpeedLimit = o.SpeedLimitKmh
            })
            .ToListAsync(ct);

        if (obs.Count == 0) return;

        var perWay = new Dictionary<long, List<Obs>>(wayIds.Length);
        foreach (var o in obs)
        {
            if (!perWay.TryGetValue(o.WayId, out var list))
            {
                list = new List<Obs>(32);
                perWay[o.WayId] = list;
            }
            list.Add(o);
        }

        var entries = perWay.ToArray();

        logger.LogDebug("Updating EMA summaries for {WayCount} ways from {ObsCount} observations", entries.Length, obs.Count);

        var results = new (long WayId, double Constrained, double Freeflow, double? SpeedLimit, DateTime LastUpdated)[entries.Length];

        Parallel.For(0, entries.Length, i =>
        {
            var (wayId, list) = entries[i];

            seeds.TryGetValue(wayId, out var seed);

            double? speedLimit = null;
            foreach (var o in list)
            {
                if (o.SpeedLimit != null)
                {
                    speedLimit = o.SpeedLimit;
                    break;
                }
            }

            double emaC = seed?.ConstrainedSpeed ?? seed?.FreeflowSpeed ?? list[0].SpeedKmh;
            foreach (var o in list)
                emaC = emaC * (1 - EmaAlpha) + o.SpeedKmh * EmaAlpha;

            double emaF = seed?.FreeflowSpeed ?? emaC;

            foreach (var o in list)
            {
                if (speedLimit is > 0 && o.SpeedKmh < speedLimit * 0.8) continue;
                emaF = emaF * (1 - EmaAlpha) + o.SpeedKmh * EmaAlpha;
            }

            if (speedLimit is > 0)
            {
                emaF = Math.Max(emaF, speedLimit.Value * 0.6);
                emaF = Math.Min(emaF, speedLimit.Value);
            }

            results[i] = (
                wayId,
                Math.Round(emaC, 1),
                Math.Round(emaF, 1),
                speedLimit,
                list[^1].Timestamp
            );
        });

        // encoding (same as before but pooled)
        var encoded = new string[results.Length];

        Parallel.For(0, results.Length, i =>
        {
            var buf = ArrayPool<float>.Shared.Rent(2016);
            try
            {
                Array.Fill(buf, (float)results[i].Freeflow);
                encoded[i] = ValhallaSpeedEncoder.Encode(buf);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buf);
            }
        });

        var wayIdsArr = new long[results.Length];
        var constrained = new double[results.Length];
        var freeflow = new double[results.Length];
        var speedLimits = new double?[results.Length];
        var lastUpdated = new DateTime[results.Length];

        for (int i = 0; i < results.Length; i++)
        {
            var r = results[i];
            wayIdsArr[i] = r.WayId;
            constrained[i] = r.Constrained;
            freeflow[i] = r.Freeflow;
            speedLimits[i] = r.SpeedLimit;
            lastUpdated[i] = r.LastUpdated;
        }

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "WaySpeedSummaries"
            ("OsmWayId","ConstrainedSpeed","FreeflowSpeed","SpeedLimitKmh","LastUpdated","EncodedSpeeds")
            SELECT * FROM unnest(@way_ids,@c,@f,@sl,@lu,@enc)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "ConstrainedSpeed" = EXCLUDED."ConstrainedSpeed",
                "FreeflowSpeed" = EXCLUDED."FreeflowSpeed",
                "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh","WaySpeedSummaries"."SpeedLimitKmh"),
                "LastUpdated" = EXCLUDED."LastUpdated",
                "EncodedSpeeds" = EXCLUDED."EncodedSpeeds"
            """,
            [
                new NpgsqlParameter("way_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = wayIdsArr },
                new NpgsqlParameter("c", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = constrained },
                new NpgsqlParameter("f", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = freeflow },
                new NpgsqlParameter("sl", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = speedLimits },
                new NpgsqlParameter("lu", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = lastUpdated },
                new NpgsqlParameter("enc", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = encoded }
            ],
            ct);
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
    /// Only includes ways updated within the last 60 minutes.
    /// </summary>
    public async Task WriteLiveTrafficCsvAsync(Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 65536, leaveOpen: true);
        await writer.WriteLineAsync("way_id,speed_kmh,freeflow_kmh");

        var cutoff = DateTime.UtcNow.AddMinutes(-60);

        await foreach (var s in readDb.WaySpeedSummaries
            .Where(s => s.LastUpdated >= cutoff)
            .OrderBy(s => s.OsmWayId)
            .AsNoTracking()
            .Select(s => new { s.OsmWayId, Speed = s.ConstrainedSpeed ?? s.FreeflowSpeed, s.FreeflowSpeed })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            await writer.WriteLineAsync($"{s.OsmWayId},{s.Speed:F1},{s.FreeflowSpeed:F1}");
        }
    }

}
