using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using OsmSharp;
using OsmSharp.Streams;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;

namespace OpenTrafficDB.Shared.Services;

/// <summary>
/// Downloads the configured OSM PBF file and imports way geometries into the
/// WayGeometry table. Runs at startup, then repeats every 30 days.
///
/// Config keys:
///   Osm:PbfUrl  — download URL (e.g. Geofabrik extract). Required.
///   Osm:PbfPath — local path to store the PBF (default: /data/map.osm.pbf).
/// </summary>
public class WayGeometryBackgroundService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<WayGeometryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan _reimportInterval = TimeSpan.FromDays(30);
    private const int UpsertBatchSize = 5_000;

    // OSM highway values to import. Covers motorways, Bundesstraßen (primary),
    // Landstraßen (secondary), main city roads (tertiary), and residential streets.
    // Excludes footways, cycleways, paths, and service roads.
    private static readonly HashSet<string> RelevantHighwayTypes =
    [
        "motorway", "motorway_link",
        "trunk",    "trunk_link",
        "primary",  "primary_link",
        "secondary","secondary_link",
        "tertiary", "tertiary_link",
        "unclassified",
        "residential",
    ];

    private string PbfPath   => config["Osm:PbfPath"] ?? "/data/map.osm.pbf";
    private string? PbfUrl   => config["Osm:PbfUrl"];

    // Marker file stores the UTC timestamp of the last successful import.
    private string MarkerPath => PbfPath + ".imported";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the DB to be ready before doing anything.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldRun())
                {
                    await DownloadAsync(stoppingToken);
                    await ImportAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PBF geometry import failed");
            }

            // Check daily whether a re-import is due.
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private bool ShouldRun()
    {
        if (string.IsNullOrWhiteSpace(PbfUrl))
        {
            logger.LogWarning("Osm:PbfUrl is not configured — way geometry import skipped");
            return false;
        }

        if (!File.Exists(MarkerPath))
            return true;

        var lastImport = DateTime.Parse(File.ReadAllText(MarkerPath).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind);
        var due = lastImport + _reimportInterval;
        if (DateTime.UtcNow < due)
        {
            logger.LogInformation("Next PBF re-import due {Due:yyyy-MM-dd}", due);
            return false;
        }

        return true;
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        var url  = PbfUrl!;
        var path = PbfPath;
        var tmp  = path + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        logger.LogInformation("Downloading PBF from {Url}", url);
        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var client = httpClientFactory.CreateClient("pbf");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total      = response.Content.Headers.ContentLength;
        var written    = 0L;
        var lastLogMb  = 0L;
        const long logEveryMb = 50;

        await using (var src  = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(tmp))
        {
            var buf = new byte[1024 * 1024]; // 1 MB chunks
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dest.WriteAsync(buf.AsMemory(0, read), ct);
                written += read;
                var currentMb = written / 1024 / 1024;
                if (total.HasValue && currentMb >= lastLogMb + logEveryMb)
                {
                    lastLogMb = currentMb / logEveryMb * logEveryMb;
                    logger.LogInformation("PBF download: {Done:N0} / {Total:N0} MB",
                        currentMb, total.Value / 1024 / 1024);
                }
            }
        }

        File.Move(tmp, path, overwrite: true);
        await File.WriteAllTextAsync(MarkerPath, DateTime.UtcNow.ToString("O"), ct);
        sw.Stop();
        logger.LogInformation("PBF download complete: {Size:N0} MB in {Elapsed}", written / 1024 / 1024, sw.Elapsed);
    }

    private async Task ImportAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting OSM PBF geometry import from {Path}", PbfPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var fileSize = new FileInfo(PbfPath).Length;

        // ── Pass 1: collect node IDs referenced by ways ───────────────────────
        // Only indexing nodes actually used by ways cuts memory dramatically
        // (typically 20-40% of all nodes on regional extracts).
        logger.LogInformation("PBF pass 1: collecting referenced node IDs...");
        var neededNodeIds = new HashSet<long>();

        await Task.Run(() =>
        {
            using var stream = File.OpenRead(PbfPath);
            var source       = new PBFOsmStreamSource(stream);
            var lastPct      = 0;

            foreach (var element in source)
            {
                if (ct.IsCancellationRequested) break;
                if (element.Type != OsmGeoType.Way) continue;
                var way = (Way)element;
                if (way.Nodes == null || way.Tags == null || !way.Tags.TryGetValue("highway", out var hwType) || !RelevantHighwayTypes.Contains(hwType)) continue;
                foreach (var nodeId in way.Nodes)
                    neededNodeIds.Add(nodeId);

                var pct = (int)(stream.Position * 100 / fileSize);
                if (pct >= lastPct + 10)
                {
                    lastPct = pct / 10 * 10;
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("PBF pass 1: {Pct}% ({Count:N0} node IDs collected)", lastPct, neededNodeIds.Count);
                }
            }
        }, ct);

        logger.LogInformation("PBF pass 1 complete: {Count:N0} node IDs needed", neededNodeIds.Count);

        // ── Pass 2: index only the needed nodes ───────────────────────────────
        logger.LogInformation("PBF pass 2: indexing nodes...");
        var nodes = new Dictionary<long, (float Lat, float Lon)>(neededNodeIds.Count);

        await Task.Run(() =>
        {
            using var stream = File.OpenRead(PbfPath);
            var source       = new PBFOsmStreamSource(stream);
            var lastPct      = 0;

            foreach (var element in source)
            {
                if (ct.IsCancellationRequested) break;
                if (element.Type != OsmGeoType.Node) continue;
                var n = (OsmSharp.Node)element;
                if (n.Id.HasValue && neededNodeIds.Contains(n.Id.Value) &&
                    n.Latitude.HasValue && n.Longitude.HasValue)
                    nodes[n.Id.Value] = ((float)n.Latitude.Value, (float)n.Longitude.Value);

                var pct = (int)(stream.Position * 100 / fileSize);
                if (pct >= lastPct + 10)
                {
                    lastPct = pct / 10 * 10;
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("PBF pass 2: {Pct}% ({Nodes:N0} nodes indexed)", lastPct, nodes.Count);
                }
            }
        }, ct);

        // Free the ID set — no longer needed.
        neededNodeIds.Clear();

        logger.LogInformation("PBF pass 2 complete: {Count:N0} nodes indexed", nodes.Count);

        // ── Pass 3: resolve ways (reader) + upsert (consumer) in parallel ─────
        logger.LogInformation("PBF pass 3: resolving ways...");

        // Channel buffers up to 4 ready batches so the reader stays ahead of the DB writer.
        var channel = System.Threading.Channels.Channel.CreateBounded<List<WayGeometry>>(4);
        var total   = 0;

        // Consumer: upserts batches from the channel as fast as the DB can accept them.
        var consumer = Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct))
            {
                await UpsertBatchAsync(batch, ct);
                total += batch.Count;
                logger.LogInformation("PBF import progress: {Total:N0} ways upserted", total);
            }
        }, ct);

        // Producer: reads PBF, resolves coordinates, writes complete batches to channel.
        await Task.Run(async () =>
        {
            var factory  = new GeometryFactory(new PrecisionModel(), 4326);
            var batch    = new List<WayGeometry>(UpsertBatchSize);
            var lastPct  = 0;

            using var stream = File.OpenRead(PbfPath);
            var source = new PBFOsmStreamSource(stream);

            foreach (var element in source)
            {
                if (ct.IsCancellationRequested) break;
                if (element.Type != OsmGeoType.Way) continue;

                var way = (Way)element;
                if (way.Id == null || way.Nodes == null || way.Nodes.Length < 2) continue;
                if (way.Tags == null || !way.Tags.TryGetValue("highway", out var hwType) || !RelevantHighwayTypes.Contains(hwType)) continue;

                var coords = new List<(float Lat, float Lon)>(way.Nodes.Length);
                foreach (var nodeId in way.Nodes)
                {
                    if (nodes.TryGetValue(nodeId, out var c))
                        coords.Add(c);
                }

                if (coords.Count < 2) continue;

                var minLat = coords.Min(c => c.Lat);
                var minLon = coords.Min(c => c.Lon);
                var maxLat = coords.Max(c => c.Lat);
                var maxLon = coords.Max(c => c.Lon);

                var bbox = factory.CreatePolygon([
                    new Coordinate(minLon, minLat),
                    new Coordinate(maxLon, minLat),
                    new Coordinate(maxLon, maxLat),
                    new Coordinate(minLon, maxLat),
                    new Coordinate(minLon, minLat)
                ]);

                var maxspeedRaw = way.Tags != null && way.Tags.ContainsKey("maxspeed")
                    ? way.Tags["maxspeed"] : null;

                var lineString = factory.CreateLineString(
                    coords.Select(c => new Coordinate(c.Lon, c.Lat)).ToArray());

                batch.Add(new WayGeometry
                {
                    OsmWayId    = way.Id.Value,
                    MinLat      = minLat,
                    MinLon      = minLon,
                    MaxLat      = maxLat,
                    MaxLon      = maxLon,
                    BBox        = bbox,
                    Geometry    = lineString,
                    MaxSpeedKmh = ParseMaxSpeed(maxspeedRaw),
                    NodeCount   = coords.Count,
                    FetchedAt   = DateTime.UtcNow
                });

                if (batch.Count >= UpsertBatchSize)
                {
                    await channel.Writer.WriteAsync(batch, ct);
                    batch = new List<WayGeometry>(UpsertBatchSize);
                }

                var pct = (int)(stream.Position * 100 / fileSize);
                if (pct >= lastPct + 10)
                {
                    lastPct = pct / 10 * 10;
                    logger.LogInformation("PBF pass 2: {Pct}%", lastPct);
                }
            }

            if (batch.Count > 0)
                await channel.Writer.WriteAsync(batch, ct);

            channel.Writer.Complete();
        }, ct);

        await consumer;

        sw.Stop();
        logger.LogInformation("PBF geometry import complete: {Total:N0} ways in {Elapsed}", total, sw.Elapsed);
    }

    private static double? ParseMaxSpeed(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var kmh) && kmh > 0)
            return kmh;

        if (raw.EndsWith(" mph", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(raw[..^4].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var mph))
            return Math.Round(mph * 1.60934, 1);

        return raw.ToLowerInvariant() switch
        {
            "de:living_street" or "living_street"   => 7,
            "de:urban"   or "fr:urban"   or "urban" => 50,
            "de:rural"   or "fr:rural"   or "rural" => 100,
            "de:motorway"                            => 130,
            "fr:motorway"                            => 130,
            "pl:urban"                               => 50,
            "pl:rural"                               => 90,
            "pl:motorway"                            => 140,
            "it:urban"                               => 50,
            "it:rural"                               => 90,
            "it:motorway"                            => 130,
            "walk"                                   => 7,
            "none" or "unlimited"                    => null,
            _                                        => null
        };
    }

    private async Task UpsertBatchAsync(List<WayGeometry> batch, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wayIds  = batch.Select(w => w.OsmWayId).ToHashSet();
        var existing = await db.WayGeometries
            .Where(g => wayIds.Contains(g.OsmWayId))
            .ToDictionaryAsync(g => g.OsmWayId, ct);

        foreach (var wg in batch)
        {
            if (existing.TryGetValue(wg.OsmWayId, out var ex))
            {
                ex.MinLat      = wg.MinLat;
                ex.MinLon      = wg.MinLon;
                ex.MaxLat      = wg.MaxLat;
                ex.MaxLon      = wg.MaxLon;
                ex.BBox        = wg.BBox;
                ex.Geometry    = wg.Geometry;
                ex.MaxSpeedKmh = wg.MaxSpeedKmh;
                ex.NodeCount   = wg.NodeCount;
                ex.FetchedAt   = wg.FetchedAt;
            }
            else
            {
                db.WayGeometries.Add(wg);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
