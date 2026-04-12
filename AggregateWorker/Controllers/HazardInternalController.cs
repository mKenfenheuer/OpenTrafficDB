using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.AggregateWorker.Services;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models.DTOs;

namespace OpenTrafficDB.AggregateWorker.Controllers;

[ApiController]
[Route("internal")]
public class HazardInternalController(
    HazardTileService tileService,
    ILogger<HazardInternalController> logger) : ControllerBase
{
    /// <summary>Hazard tile in MVT format (gzip-compressed protobuf).</summary>
    [HttpGet("tiles/hazards/current/{z}/{x}/{y}")]
    public async Task<IActionResult> GetHazardTile(int z, int x, int y, CancellationToken ct)
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Hazard tile {Z}/{X}/{Y} build failed", z, x, y);
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Returns all active (non-expired) hazard aggregates as JSON.
    /// Supports optional bbox filtering via query parameters.
    /// </summary>
    [HttpGet("hazards/live/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetActiveHazards(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_hazards.csv\"";
        await tileService.WriteHazardsLiveCsv(Response.Body, ct);
        return new EmptyResult();
    }
}
