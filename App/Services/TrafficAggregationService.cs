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
    OpenTrafficDB.App.Pipeline.SpeedProfileWriter profileWriter,
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
    /// Streams the Valhalla predicted-traffic CSV. The prediction of each road is computed here from
    /// its weekly profile and current free-flow speed (<see cref="OpenTrafficDB.App.Pipeline.SpeedProfileWriter.Predict"/>,
    /// DCT encoding), in parallel per block of rows, so free-flow corrections take effect at once
    /// and the profile flush does not rewrite 1.4M stored encodings every 30 minutes.
    /// </summary>
    public async Task WriteValhallaExportCsvAsync(Stream output, CancellationToken ct = default)
    {
        const int Block = 20_000;
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 1 << 16, leaveOpen: true);
        await writer.WriteLineAsync("way_id,freeflow_speed,constrained_speed,historical_speeds");

        var conn = (NpgsqlConnection)readDb.Database.GetDbConnection();
        await readDb.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT p."OsmWayId", p."Profile", p."FirstSeenAt", s."FreeflowSpeed", s."ConstrainedSpeed"
            FROM "WayProfiles" p
            JOIN "WaySpeedSummaries" s ON s."OsmWayId" = p."OsmWayId"
            ORDER BY p."OsmWayId"
            """, conn) { CommandTimeout = 1800 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var now = DateTime.UtcNow;
        var ids = new long[Block]; var profiles = new byte[Block][]; var firsts = new DateTime[Block];
        var freeflows = new double[Block]; var constrained = new double[Block]; var lines = new string[Block];
        bool more = true;
        while (more)
        {
            int n = 0;
            while (n < Block && (more = await reader.ReadAsync(ct)))
            {
                ids[n] = reader.GetInt64(0);
                profiles[n] = (byte[])reader[1];
                firsts[n] = reader.GetDateTime(2);
                freeflows[n] = reader.GetDouble(3);
                constrained[n] = reader.IsDBNull(4) ? freeflows[n] : reader.GetDouble(4);
                n++;
            }
            if (n == 0) break;
            Parallel.For(0, n, () => (new float[WayProfile.Slots], new byte[WayProfileCodec.RawLength]), (i, _, buffers) =>
            {
                var (speeds, raw) = buffers;
                try { WayProfileCodec.Decode(profiles[i], raw); }
                catch (FormatException) { lines[i] = null!; return buffers; }
                profileWriter.Predict(raw, freeflows[i], firsts[i], now, speeds);
                lines[i] = FormattableString.Invariant(
                    $"{ids[i]},{(int)Math.Round(freeflows[i])},{(int)Math.Round(constrained[i])},{ValhallaSpeedEncoder.Encode(speeds)}");
                return buffers;
            }, _ => { });
            for (int i = 0; i < n; i++)
                if (lines[i] != null) await writer.WriteLineAsync(lines[i]);
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

    /// <summary>
    /// Writes the length of every way with geometry as <c>way_id,length_m</c>. The router weights
    /// the member ways of its compacted edges by length (a 120 m way in a jam must not count as
    /// much as a 1.9 km one).
    /// </summary>
    public async Task WriteWayLengthsCsvAsync(Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 65536, leaveOpen: true);
        await writer.WriteLineAsync("way_id,length_m");

        var conn = (NpgsqlConnection)readDb.Database.GetDbConnection();
        await readDb.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT "OsmWayId", ST_Length("Geometry"::geography)
            FROM "WayGeometries" WHERE "Geometry" IS NOT NULL
            """, conn) { CommandTimeout = 600 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            await writer.WriteLineAsync(FormattableString.Invariant($"{reader.GetInt64(0)},{reader.GetDouble(1):F0}"));
    }
}
