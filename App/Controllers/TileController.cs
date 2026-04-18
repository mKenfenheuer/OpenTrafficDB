using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Serves Mapbox Vector Tiles (MVT) for traffic overlays.
/// Tiles are publicly accessible — no authentication required.
/// </summary>
[ApiController]
[Route("tiles")]
[EnableCors("Tiles")]
public class TileController(
    TrafficTileService trafficTiles,
    HazardTileService hazardTiles,
    IConfiguration config) : ControllerBase
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
    /// Compatible with Maputnik and any MapLibre GL client.
    /// </summary>
    [HttpGet("traffic/current/style.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStyleJson()
    {
        var baseUrl    = config["BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var tileJsonUrl = $"{baseUrl}/tiles/traffic/current.json";

        var json = $$"""
            {
              "version": 8,
              "name": "OpenTrafficDB Traffic",
              "sources": {
                "traffic": {
                  "type": "vector",
                  "url": "{{tileJsonUrl}}"
                }
              },
              "layers": [
                {
                  "id": "traffic-lines",
                  "type": "line",
                  "source": "traffic",
                  "source-layer": "traffic",
                  "minzoom": 6,
                  "paint": {
                    "line-color": [
                      "interpolate",
                      ["linear"],
                      ["coalesce", ["to-number", ["get", "jam_estimate"]], 0],
                      0.0, ["rgba", 0, 200, 0, 1],
                      0.5, ["rgba", 255, 165, 0, 1],
                      1.0, ["rgba", 255, 0, 0, 1]
                    ],
                    "line-width": [
                      "interpolate",
                      ["linear"],
                      ["zoom"],
                      6, 1,
                      10, 2,
                      14, 4
                    ],
                    "line-opacity": 0.9
                  }
                }
              ]
            }
            """;

        return Content(json, "application/json");
    }

    // ── Hazard tiles ──────────────────────────────────────────────────────────

    /// <summary>
    /// TileJSON 3.0 metadata for the hazards overlay.
    /// </summary>
    [HttpGet("hazards/current.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHazardTileJson()
    {
        var baseUrl = config["BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            tilejson    = "3.0.0",
            name        = "OpenTrafficDB — Hazards",
            description = "User-reported traffic hazards: cameras, accidents, police, road works, and more.",
            version     = "1.0.0",
            attribution = "© OpenTrafficDB contributors, © OpenStreetMap contributors",
            scheme      = "xyz",
            tiles       = new[] { $"{baseUrl}/tiles/hazards/current/{{z}}/{{x}}/{{y}}" },
            minzoom     = 6,
            maxzoom     = 16,
            bounds      = new[] { -180.0, -85.0511, 180.0, 85.0511 },
            vector_layers = new[]
            {
                new
                {
                    id          = "hazards",
                    description = "Point features representing active traffic hazards.",
                    minzoom     = 6,
                    maxzoom     = 16,
                    fields      = new Dictionary<string, string>
                    {
                        ["osm_way_id"]          = "OSM way ID of the matched edge",
                        ["hazard_type"]         = "Hazard type (SpeedCamera, Accident, PoliceControl, etc.)",
                        ["metadata"]            = "JSON metadata (e.g. max_speed for cameras)",
                        ["confirmation_count"]  = "Number of user confirmations",
                        ["last_confirmed_epoch"] = "Unix epoch of last confirmation",
                        ["expires_epoch"]        = "Unix epoch when hazard expires",
                    }
                }
            }
        });
    }

    /// <summary>
    /// MapLibre GL style JSON for the hazards overlay.
    /// </summary>
    [HttpGet("hazards/current/style.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHazardStyleJson()
    {
        var baseUrl     = config["BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var tileJsonUrl = $"{baseUrl}/tiles/hazards/current.json";

        var json = $$"""
            {
              "version": 8,
              "name": "OpenTrafficDB Hazards",
              "sources": {
                "hazards": {
                  "type": "vector",
                  "url": "{{tileJsonUrl}}"
                }
              },
              "layers": [
                {
                  "id": "hazard-points",
                  "type": "circle",
                  "source": "hazards",
                  "source-layer": "hazards",
                  "minzoom": 6,
                  "paint": {
                    "circle-radius": [
                      "interpolate", ["linear"], ["zoom"],
                      6, 3,
                      10, 5,
                      14, 8
                    ],
                    "circle-color": [
                      "match", ["get", "hazard_type"],
                      "SpeedCamera",      "#ff6600",
                      "DistanceCamera",   "#ff6600",
                      "RedLightCamera",   "#ff0000",
                      "MultiCamera",      "#ff3300",
                      "SectionControl",   "#ff6600",
                      "WeightControl",    "#996633",
                      "Accident",         "#cc0000",
                      "BrokenDownCar",    "#cc6600",
                      "Debris",           "#999900",
                      "Pedestrian",       "#0066cc",
                      "PoliceGeneral",    "#0000cc",
                      "PoliceRoadBlock",  "#0000ff",
                      "PoliceControl",    "#3333ff",
                      "PoliceAlcohol",    "#6600cc",
                      "RoadWorks",        "#ff9900",
                      "TrafficJam",       "#cc0000",
                      "#888888"
                    ],
                    "circle-opacity": 0.9,
                    "circle-stroke-width": 1,
                    "circle-stroke-color": "#ffffff"
                  }
                }
              ]
            }
            """;

        return Content(json, "application/json");
    }

    /// <summary>
    /// Hazard overlay tile in Mapbox Vector Tile format (gzip-compressed protobuf).
    /// </summary>
    [HttpGet("hazards/current/{z}/{x}/{y}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetHazardTile(int z, int x, int y, CancellationToken ct)
    {
        if (z is < 0 or > 22) return BadRequest(new { error = "Zoom level must be between 0 and 22." });

        int maxCoord = 1 << z;
        if (x < 0 || x >= maxCoord || y < 0 || y >= maxCoord)
            return BadRequest(new { error = $"Tile coordinates out of range for zoom {z}." });

        var bytes = await hazardTiles.BuildTileAsync(z, x, y, ct);
        Response.Headers["Content-Encoding"] = "gzip";
        Response.Headers["Cache-Control"] = "public, max-age=60";
        return File(bytes, "application/vnd.mapbox-vector-tile");
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

        var bytes = await trafficTiles.BuildTileAsync(z, x, y, ct);
        Response.Headers["Content-Encoding"] = "gzip";
        Response.Headers["Cache-Control"] = "public, max-age=60";
        return File(bytes, "application/vnd.mapbox-vector-tile");
    }
}
