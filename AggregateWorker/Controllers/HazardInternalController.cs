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
    ReadOnlyAppDbContext db,
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
    [HttpGet("hazards/active")]
    public async Task<IActionResult> GetActiveHazards(
        [FromQuery] double? minLat, [FromQuery] double? minLon,
        [FromQuery] double? maxLat, [FromQuery] double? maxLon,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.HazardAggregates
            .Where(h => h.ExpiresAt > now);

        if (minLat.HasValue && minLon.HasValue && maxLat.HasValue && maxLon.HasValue)
        {
            query = query.Where(h =>
                h.Latitude >= minLat.Value && h.Latitude <= maxLat.Value &&
                h.Longitude >= minLon.Value && h.Longitude <= maxLon.Value);
        }

        var hazards = await query
            .OrderByDescending(h => h.LastConfirmedAt)
            .Take(10_000)
            .Select(h => new ActiveHazardDto
            {
                OsmWayId          = h.OsmWayId,
                HazardType        = h.HazardType,
                Latitude          = h.Latitude,
                Longitude         = h.Longitude,
                Metadata          = h.Metadata,
                ConfirmationCount = h.ConfirmationCount,
                LastConfirmedAt   = h.LastConfirmedAt,
                ExpiresAt         = h.ExpiresAt
            })
            .ToListAsync(ct);

        return Ok(hazards);
    }
}
