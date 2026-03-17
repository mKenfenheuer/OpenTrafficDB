using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Serves Mapbox Vector Tiles (MVT) for traffic overlays.
/// Tiles are publicly accessible — no authentication required.
/// </summary>
[ApiController]
[Route("tiles")]
public class TileController(TrafficTileService tileService) : ControllerBase
{
    /// <summary>
    /// Current traffic overlay tile in Mapbox Vector Tile format (gzip-compressed protobuf).
    ///
    /// Layer: <c>traffic</c> — LineString per OSM way with traffic attributes:
    /// <list type="bullet">
    ///   <item><c>osm_way_id</c> (Number) — OSM way identifier</item>
    ///   <item><c>speed_kmh</c> (Number) — observed average speed</item>
    ///   <item><c>speed_limit_kmh</c> (Number, optional) — speed limit</item>
    ///   <item><c>delay_factor</c> (Number, optional) — 0=free flow, 1=standing</item>
    ///   <item><c>jam_estimate</c> (Number, optional) — 0=no jam, 1=standing traffic</item>
    ///   <item><c>source</c> (String) — "live" or "historic"</item>
    ///   <item><c>live_samples</c> (Number) — number of live observations</item>
    ///   <item><c>historic_samples</c> (Number) — number of historic observations</item>
    ///   <item><c>osm_max_speed</c> (Number, optional) — parsed OSM maxspeed tag in km/h</item>
    /// </list>
    ///
    /// Tile coordinates follow the standard XYZ/Slippy-map convention (Y=0 at north).
    /// Zoom levels 8–16 recommended; below zoom 8 tiles may be sparse.
    /// </summary>
    [HttpGet("traffic/current/{z}/{x}/{y}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCurrentTrafficTile(
        int z, int x, int y,
        CancellationToken ct)
    {
        if (z is < 0 or > 22) return BadRequest(new { error = "Zoom level must be between 0 and 22." });

        int maxCoord = 1 << z;
        if (x < 0 || x >= maxCoord || y < 0 || y >= maxCoord)
            return BadRequest(new { error = $"Tile coordinates out of range for zoom {z}." });

        try
        {
            var tileBytes = await tileService.BuildTileAsync(z, x, y, ct);
            Response.Headers["Content-Encoding"] = "gzip";
            Response.Headers["Cache-Control"]    = "public, max-age=60";
            return File(tileBytes, "application/vnd.mapbox-vector-tile");
        }
        catch (OperationCanceledException)
        {
            return NoContent();
        }
    }
}
