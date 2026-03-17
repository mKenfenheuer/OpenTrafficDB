using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.AggregateWorker.Services;

namespace OpenTrafficDB.AggregateWorker.Controllers;

[ApiController]
[Route("internal/tiles")]
public class TileInternalController(TrafficTileService tileService) : ControllerBase
{
    [HttpGet("traffic/current/{z}/{x}/{y}")]
    public async Task<IActionResult> GetCurrentTrafficTile(int z, int x, int y, CancellationToken ct)
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
