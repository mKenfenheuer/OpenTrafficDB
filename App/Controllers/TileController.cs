using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Serves Mapbox Vector Tiles (MVT) for traffic overlays.
/// Tiles are publicly accessible — no authentication required.
/// </summary>
[ApiController]
[Route("tiles")]
[EnableCors("Tiles")]
public class TileController(WorkerClient workers, IConfiguration config) : ControllerBase
{
    /// <summary>
    /// TileJSON 3.0 metadata for the current traffic overlay.
    /// Compatible with MapLibre GL, Maputnik, and any TileJSON-aware client.
    /// </summary>
    [HttpGet("traffic/current.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetTileJson()
    {
        var baseUrl = config["BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            tilejson    = "3.0.0",
            name        = "OpenTrafficDB — Current Traffic",
            description = "Live and historic traffic speeds map-matched to OpenStreetMap way geometries.",
            version     = "1.0.0",
            attribution = "© OpenTrafficDB contributors, © OpenStreetMap contributors",
            scheme      = "xyz",
            tiles       = new[] { $"{baseUrl}/tiles/traffic/current/{{z}}/{{x}}/{{y}}" },
            minzoom     = 6,
            maxzoom     = 16,
            bounds      = new[] { -180.0, -85.0511, 180.0, 85.0511 },
            vector_layers = new[]
            {
                new
                {
                    id          = "traffic",
                    description = "Road segments with speed and congestion data.",
                    minzoom     = 6,
                    maxzoom     = 16,
                    fields      = new Dictionary<string, string>
                    {
                        ["osm_way_id"]    = "OSM way ID",
                        ["speed_kmh"]     = "Current speed in km/h",
                        ["freeflow_kmh"]  = "Historic free-flow speed in km/h",
                        ["p50_kmh"]       = "Median observed speed in km/h",
                        ["p95_kmh"]       = "95th-percentile speed in km/h",
                        ["speed_limit_kmh"] = "Speed limit in km/h (when available)",
                        ["delay_factor"]  = "Delay ratio 0–1 (0 = free flow, 1 = standstill)",
                        ["jam_estimate"]  = "Congestion estimate 0–1",
                        ["source"]        = "live | historic",
                        ["total_samples"] = "Number of observations used",
                    }
                }
            }
        });
    }

    /// <summary>
    /// MapLibre GL style JSON for the current traffic overlay.
    /// Uses a plain dark background — add your own base map source as needed.
    /// </summary>
    [HttpGet("traffic/current/style.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStyleJson()
    {
        var baseUrl = config["BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var tileJsonUrl = $"{baseUrl}/tiles/traffic/current.json";

        return Ok(new
        {
            version = 8,
            name    = "OpenTrafficDB Traffic",
            glyphs  = "https://demotiles.maplibre.org/font/{fontstack}/{range}.pbf",
            sources = new Dictionary<string, object>
            {
                ["traffic"] = new
                {
                    type    = "vector",
                    url     = tileJsonUrl,
                }
            },
            layers = new object[]
            {
                new
                {
                    id   = "background",
                    type = "background",
                    paint = new { background_color = "#1a1a2e" }
                },
                new
                {
                    id     = "traffic-lines-casing",
                    type   = "line",
                    source = "traffic",
                    source_layer = "traffic",
                    minzoom = 6,
                    layout = new
                    {
                        line_cap  = "round",
                        line_join = "round"
                    },
                    paint = new
                    {
                        line_color   = "#000000",
                        line_opacity = 0.3,
                        line_width   = new object[]
                        {
                            "interpolate", new[] { "linear" }, new[] { "zoom" },
                            8, 4, 12, 7, 16, 12
                        }
                    }
                },
                new
                {
                    id     = "traffic-lines",
                    type   = "line",
                    source = "traffic",
                    source_layer = "traffic",
                    minzoom = 6,
                    layout = new
                    {
                        line_cap  = "round",
                        line_join = "round"
                    },
                    paint = new
                    {
                        line_width = new object[]
                        {
                            "interpolate", new[] { "linear" }, new[] { "zoom" },
                            8, 2, 12, 4, 16, 7
                        },
                        line_color = new object[]
                        {
                            "case",
                            new object[] { "has", "jam_estimate" },
                            new object[]
                            {
                                "interpolate", new[] { "linear" },
                                new[] { "get", "jam_estimate" },
                                0.0,  "#3fb950",
                                0.35, "#d29922",
                                0.70, "#f85149"
                            },
                            "#8b949e"
                        },
                        line_opacity = 0.85
                    }
                }
            }
        });
    }

    /// <summary>
    /// Current traffic overlay tile in Mapbox Vector Tile format (gzip-compressed protobuf).
    /// </summary>
    [HttpGet("traffic/current/{z}/{x}/{y}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCurrentTrafficTile(int z, int x, int y, CancellationToken ct)
    {
        if (z is < 0 or > 22) return BadRequest(new { error = "Zoom level must be between 0 and 22." });

        int maxCoord = 1 << z;
        if (x < 0 || x >= maxCoord || y < 0 || y >= maxCoord)
            return BadRequest(new { error = $"Tile coordinates out of range for zoom {z}." });

        await workers.ProxyPublicAsync("Aggregate",
            $"/internal/tiles/traffic/current/{z}/{x}/{y}", Response, ct);
        return new EmptyResult();
    }
}
