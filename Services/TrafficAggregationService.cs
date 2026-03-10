using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;

namespace OpenTrafficDB.Services;

public class TrafficAggregationService(AppDbContext db)
{
    /// <summary>
    /// Recompute aggregates for all matched observations that have a ValhallaEdgeId.
    /// Called by the background service periodically.
    /// </summary>
    public async Task ReaggregateAsync(CancellationToken ct = default)
    {
        // Fetch all observations with a matched edge ID, streamed to avoid huge memory usage
        var matched = await db.SpeedObservations
            .Where(o => o.ValhallaEdgeId != null)
            .Select(o => new
            {
                o.ValhallaEdgeId,
                o.Timestamp,
                o.SpeedMs
            })
            .ToListAsync(ct);

        if (matched.Count == 0) return;

        // Compute new aggregates in memory
        var buckets = new Dictionary<(long EdgeId, int DayOfWeek, int Bucket), (double SumSpeed, int Count)>();

        foreach (var obs in matched)
        {
            var ts = obs.Timestamp.ToUniversalTime();
            var dow = (int)ts.DayOfWeek;
            var bucket = (ts.Hour * 60 + ts.Minute) / 5;
            var key = (obs.ValhallaEdgeId!.Value, dow, bucket);

            if (buckets.TryGetValue(key, out var agg))
                buckets[key] = (agg.SumSpeed + obs.SpeedMs * 3.6, agg.Count + 1);
            else
                buckets[key] = (obs.SpeedMs * 3.6, 1);
        }

        // Upsert into RoadSpeedAggregates
        var now = DateTime.UtcNow;
        foreach (var kvp in buckets)
        {
            var (edgeId, dow, bucket) = kvp.Key;
            var (sumSpeed, count) = kvp.Value;
            var avgKmh = sumSpeed / count;

            var existing = await db.RoadSpeedAggregates
                .FirstOrDefaultAsync(a => a.ValhallaEdgeId == edgeId
                    && a.DayOfWeek == dow
                    && a.FiveMinuteBucket == bucket, ct);

            if (existing == null)
            {
                db.RoadSpeedAggregates.Add(new RoadSpeedAggregate
                {
                    ValhallaEdgeId = edgeId,
                    DayOfWeek = dow,
                    FiveMinuteBucket = bucket,
                    AvgSpeedKmh = avgKmh,
                    SampleCount = count,
                    LastUpdated = now
                });
            }
            else
            {
                existing.AvgSpeedKmh = avgKmh;
                existing.SampleCount = count;
                existing.LastUpdated = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Export predicted traffic in Valhalla CSV format.
    /// Format: edgeid,speed_bucket_0,...,speed_bucket_2015
    /// Speed in km/h, bucket = dayOfWeek * 288 + minuteOfDay / 5
    /// Use with: valhalla_add_predicted_traffic
    /// </summary>
    public async Task WritePredictedCsvAsync(Stream output, CancellationToken ct = default)
    {
        var aggregates = await db.RoadSpeedAggregates
            .OrderBy(a => a.ValhallaEdgeId)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);

        var byEdge = aggregates.GroupBy(a => a.ValhallaEdgeId);
        foreach (var edge in byEdge)
        {
            var speeds = new double[2016];
            Array.Fill(speeds, -1.0);

            foreach (var agg in edge)
            {
                var globalBucket = agg.DayOfWeek * 288 + agg.FiveMinuteBucket;
                if (globalBucket is >= 0 and < 2016)
                    speeds[globalBucket] = agg.AvgSpeedKmh;
            }

            // Fill unknown buckets with the overall average for this edge
            var known = speeds.Where(s => s >= 0).ToArray();
            if (known.Length == 0) continue;
            var avg = known.Average();
            for (int i = 0; i < speeds.Length; i++)
                if (speeds[i] < 0) speeds[i] = avg;

            var sb = new StringBuilder();
            sb.Append(edge.Key);
            foreach (var s in speeds)
            {
                sb.Append(',');
                sb.Append((int)Math.Round(s));
            }
            await writer.WriteLineAsync(sb, ct);
        }
    }

    /// <summary>
    /// Get recent speed observations within a bounding box for live traffic display.
    /// </summary>
    public async Task<LiveTrafficResponse> GetLiveSpeedsAsync(LiveTrafficQuery query, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-query.MaxAgeMinutes);

        var obs = await db.SpeedObservations
            .Where(o => o.Timestamp >= cutoff
                && o.Latitude >= query.MinLat && o.Latitude <= query.MaxLat
                && o.Longitude >= query.MinLon && o.Longitude <= query.MaxLon)
            .OrderByDescending(o => o.Timestamp)
            .Take(10_000)
            .Select(o => new LiveSpeedPoint
            {
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                SpeedMs = o.SpeedMs,
                SpeedKmh = Math.Round(o.SpeedMs * 3.6, 1),
                Heading = o.Heading,
                ValhallaEdgeId = o.ValhallaEdgeId,
                Timestamp = o.Timestamp
            })
            .ToListAsync(ct);

        return new LiveTrafficResponse
        {
            Points = obs,
            TotalPoints = obs.Count,
            AsOf = DateTime.UtcNow
        };
    }
}
