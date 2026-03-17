using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Serves Mapbox Vector Tiles (MVT) for traffic overlays.
/// Tiles are publicly accessible — no authentication required.
/// </summary>
[ApiController]
[Route("tiles")]
public class TileController(WorkerClient workers) : ControllerBase
{
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
