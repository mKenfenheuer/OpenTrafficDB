using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.AggregateWorker.Services;

public class TrafficAggregationService(
    AppDbContext db,
    ReadOnlyAppDbContext readDb,
    ILogger<TrafficAggregationService> logger)
{
    // ── Time-bucket helpers ─────────────────────────────────────────────────────

    private static readonly TimeSpan UnmatchedRetention = TimeSpan.FromHours(24);

    // ── Hybrid aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates matched observations in [from, to) time window using server-side SQL upserts.
    /// Does NOT run cleanup — call <see cref="FinalizeAsync"/> after all slots complete.
    /// Returns the number of observations processed.
    /// </summary>
    public async Task<AggregateJobResult> ReaggregateRangeAsync(
        long[] wayIds, CancellationToken ct = default)
    {
        if (wayIds.Length == 0) return new AggregateJobResult(0);

        logger.LogDebug("Aggregating {WayCount} ways", wayIds.Length);

        // Chunks are partitioned by way ID at the coordinator, so no two concurrent jobs
        // share a way — no deadlocks possible on WaySpeedAggregates.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "WaySpeedAggregates"
                ("OsmWayId", "DayOfWeek", "FiveMinuteBucket",
                 "SumSpeedKmh", "SampleCount", "SpeedLimitKmh", "LastUpdated", "FirstSeenAt")
            SELECT
                "OsmWayId",
                EXTRACT(DOW FROM "Timestamp")::int,
                (EXTRACT(HOUR FROM "Timestamp")::int * 60
                    + EXTRACT(MINUTE FROM "Timestamp")::int) / 5,
                SUM("SpeedMs" * 3.6),
                COUNT(*)::int,
                MAX("SpeedLimitKmh"),
                MAX("Timestamp"),
                MIN("Timestamp")
            FROM "SpeedObservations"
            WHERE NOT "IsAggregated"
              AND "OsmWayId" = ANY({wayIds})
            GROUP BY
                "OsmWayId",
                EXTRACT(DOW FROM "Timestamp"),
                (EXTRACT(HOUR FROM "Timestamp")::int * 60
                    + EXTRACT(MINUTE FROM "Timestamp")::int) / 5
            ON CONFLICT ("OsmWayId", "DayOfWeek", "FiveMinuteBucket") DO UPDATE SET
                "SumSpeedKmh"   = "WaySpeedAggregates"."SumSpeedKmh"  + EXCLUDED."SumSpeedKmh",
                "SampleCount"   = "WaySpeedAggregates"."SampleCount"  + EXCLUDED."SampleCount",
                "SpeedLimitKmh" = COALESCE(EXCLUDED."SpeedLimitKmh", "WaySpeedAggregates"."SpeedLimitKmh"),
                "LastUpdated"   = EXCLUDED."LastUpdated"
            """, ct);

        // Apply EMA to WaySpeedSummaries before observations are deleted, then delete.
        await UpdateSummariesEmaAsync(wayIds, ct);

        var processed = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && wayIds.Contains(o.OsmWayId.Value))
            .ExecuteDeleteAsync(ct);

        if (processed > 0)
        {
            db.AggregationMetrics.Add(new AggregationMetric
            {
                ProcessedAt       = DateTime.UtcNow,
                ObservationCount  = processed,
                WayCount          = wayIds.Length,
            });
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Aggregated {WayCount} ways, deleted {ObsCount} observations", wayIds.Length, processed);
        return new AggregateJobResult(processed);
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

    // ── WaySpeedSummary EMA maintenance ─────────────────────────────────────────

    private const double EmaAlpha = 0.8;

    private static double BlendedAvg(double sumSpeedKmh, int sampleCount, DateTime firstSeenAt, double freeflowSpeed, DateTime now)
    {
        var totalSlots = Math.Max(1, (int)Math.Ceiling((now - firstSeenAt).TotalDays / 7.0));
        var missing    = Math.Max(0, totalSlots - sampleCount);
        return (sumSpeedKmh + missing * freeflowSpeed) / (sampleCount + missing);
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

        // Load existing summaries as EMA seeds.
        var seeds = await db.WaySpeedSummaries
            .Where(s => wayIds.Contains(s.OsmWayId))
            .AsNoTracking()
            .ToDictionaryAsync(s => s.OsmWayId, ct);

        // Load all pending observations ordered oldest-first for sequential EMA application.
        var obs = await db.SpeedObservations
            .Where(o => !o.IsAggregated && o.OsmWayId != null && wayIds.Contains(o.OsmWayId.Value))
            .OrderBy(o => o.Timestamp)
            .Select(o => new { WayId = o.OsmWayId!.Value, SpeedKmh = o.SpeedMs * 3.6, o.SpeedLimitKmh, o.Timestamp })
            .AsNoTracking()
            .ToListAsync(ct);

        if (obs.Count == 0) return;

        var groups = obs.GroupBy(o => o.WayId).ToArray();
        logger.LogDebug("Updating EMA summaries for {WayCount} ways from {ObsCount} observations", groups.Length, obs.Count);

        // Result slot per group — pre-allocated so Parallel.For can write by index without locks.
        var results = new (long WayId, double Constrained, double Freeflow, double? SpeedLimit, DateTime LastUpdated)[groups.Length];

        Parallel.For(0, groups.Length, i =>
        {
            var group      = groups[i];
            var wayId      = group.Key;
            var speedLimit = group.FirstOrDefault(o => o.SpeedLimitKmh != null)?.SpeedLimitKmh;

            seeds.TryGetValue(wayId, out var seed);

            // ConstrainedSpeed: sequential EMA over all observations, oldest first.
            double emaConstrained = seed?.ConstrainedSpeed ?? seed?.FreeflowSpeed ?? group.First().SpeedKmh;
            foreach (var o in group)
                emaConstrained = emaConstrained * (1 - EmaAlpha) + o.SpeedKmh * EmaAlpha;

            // FreeflowSpeed: sequential EMA of observations within 20% of speed limit
            // (i.e. not in congestion). Falls back to all observations when no limit is known.
            var freeflowObs = speedLimit is > 0
                ? group.Where(o => o.SpeedKmh >= speedLimit.Value * 0.8).ToList()
                : group.ToList();

            double emaFreeflow = seed?.FreeflowSpeed ?? (freeflowObs.Count > 0 ? freeflowObs[0].SpeedKmh : emaConstrained);
            foreach (var o in freeflowObs)
                emaFreeflow = emaFreeflow * (1 - EmaAlpha) + o.SpeedKmh * EmaAlpha;

            // Freeflow must not drop below 60% of speed limit — protects against
            // systematic low-sample EMA drift dragging freeflow into congested range.
            if (speedLimit is > 0)
                emaFreeflow = Math.Max(emaFreeflow, speedLimit.Value * 0.6);

            results[i] = (wayId, Math.Round(emaConstrained, 1), Math.Round(emaFreeflow, 1),
                          speedLimit, group.Max(o => o.Timestamp));
        });

        // Load current aggregate buckets for these ways (post-upsert state) to compute encoded speeds.
        var aggregates = await db.WaySpeedAggregates
            .Where(a => wayIds.Contains(a.OsmWayId) && a.SampleCount > 0)
            .AsNoTracking()
            .Select(a => new { a.OsmWayId, a.DayOfWeek, a.FiveMinuteBucket, a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt })
            .ToListAsync(ct);

        var aggregatesByWay = aggregates
            .GroupBy(a => a.OsmWayId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Encode per-way speed patterns in parallel — each iteration is CPU-bound and independent.
        var encodedSpeeds = new string[results.Length];
        Parallel.For(0, results.Length, i =>
        {
            var r    = results[i];
            var buf  = new float[2016];
            var fill = (float)r.Freeflow;
            Array.Fill(buf, fill);
            // Track which slots have real data vs freeflow fill
            var hasData = new bool[2016];
            if (aggregatesByWay.TryGetValue(r.WayId, out var wayAggs))
            {
                foreach (var a in wayAggs)
                {
                    int idx = (a.DayOfWeek + 6) % 7 * 288 + a.FiveMinuteBucket;
                    buf[idx]     = (float)BlendedAvg(a.SumSpeedKmh, a.SampleCount, a.FirstSeenAt, r.Freeflow, now);
                    hasData[idx] = true;
                }

                // Fill gaps from nearest populated bucket within ±60 min (12 slots), same day
                for (int day = 0; day < 7; day++)
                {
                    int dayBase = day * 288;
                    for (int slot = 0; slot < 288; slot++)
                    {
                        if (hasData[dayBase + slot]) continue;
                        for (int d = 1; d <= 12; d++)
                        {
                            int before = dayBase + ((slot - d) % 288 + 288) % 288;
                            if (hasData[before]) { buf[dayBase + slot] = buf[before]; break; }
                            int after = dayBase + (slot + d) % 288;
                            if (hasData[after])  { buf[dayBase + slot] = buf[after];  break; }
                        }
                    }
                }
            }
            encodedSpeeds[i] = ValhallaSpeedEncoder.Encode(buf);
        });

        var wayIds_out  = new long  [results.Length];
        var constrained = new double[results.Length];
        var freeflow    = new double[results.Length];
        var speedLimits = new double?[results.Length];
        var lastUpdated = new DateTime[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            wayIds_out [i] = r.WayId;
            constrained[i] = r.Constrained;
            freeflow   [i] = r.Freeflow;
            speedLimits[i] = r.SpeedLimit;
            lastUpdated[i] = r.LastUpdated;
        }

        // Bulk upsert via unnest — one round-trip for all ways.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "WaySpeedSummaries"
                ("OsmWayId", "ConstrainedSpeed", "FreeflowSpeed", "SpeedLimitKmh", "LastUpdated", "EncodedSpeeds")
            SELECT v.way_id, v.constrained, v.freeflow, v.speed_limit, v.last_updated, v.encoded_speeds
            FROM   unnest(@way_ids, @constrained, @freeflow, @speed_limits, @last_updated, @encoded_speeds)
                   AS v(way_id, constrained, freeflow, speed_limit, last_updated, encoded_speeds)
            ON CONFLICT ("OsmWayId") DO UPDATE SET
                "ConstrainedSpeed" = EXCLUDED."ConstrainedSpeed",
                "FreeflowSpeed"    = EXCLUDED."FreeflowSpeed",
                "SpeedLimitKmh"    = COALESCE(EXCLUDED."SpeedLimitKmh", "WaySpeedSummaries"."SpeedLimitKmh"),
                "LastUpdated"      = EXCLUDED."LastUpdated",
                "EncodedSpeeds"    = EXCLUDED."EncodedSpeeds"
            """,
            [
                new NpgsqlParameter("way_ids",       NpgsqlDbType.Array | NpgsqlDbType.Bigint)      { Value = wayIds_out  },
                new NpgsqlParameter("constrained",   NpgsqlDbType.Array | NpgsqlDbType.Double)      { Value = constrained },
                new NpgsqlParameter("freeflow",      NpgsqlDbType.Array | NpgsqlDbType.Double)      { Value = freeflow    },
                new NpgsqlParameter("speed_limits",  NpgsqlDbType.Array | NpgsqlDbType.Double)      { Value = speedLimits },
                new NpgsqlParameter("last_updated",  NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = lastUpdated },
                new NpgsqlParameter("encoded_speeds",NpgsqlDbType.Array | NpgsqlDbType.Text)        { Value = encodedSpeeds },
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
            var freeflow    = (int)Math.Round(s.FreeflowSpeed);
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
