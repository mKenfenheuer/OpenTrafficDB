using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>
/// Endpoints for consuming aggregated traffic data, compatible with Valhalla routing.
/// </summary>
[ApiController]
[Route("api/v1/traffic")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class TrafficController(
    TrafficAggregationService aggregation,
    AbuseDetectionService abuseDetection) : ControllerBase
{
    /// <summary>
    /// Export predicted traffic speeds in Valhalla CSV format (single flat file).
    /// Format: edge_id,freeflow_speed,constrained_speed
    ///   edge_id is the Valhalla GraphId string "level/tile_id/id"
    ///   freeflow_speed = typical night-time speed (km/h)
    ///   constrained_speed = typical day-time speed (km/h)
    /// </summary>
    [HttpGet("predicted/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPredictedCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded for data export." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"predicted_traffic.csv\"";

        await aggregation.WritePredictedCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Download a traffic.tar archive organised in Valhalla's tile folder hierarchy.
    /// Extract it to a folder and pass that folder to valhalla_add_predicted_traffic:
    ///   tar -xf traffic.tar -C /path/to/traffic_tiles/
    ///   valhalla_add_predicted_traffic --config valhalla.json -t /path/to/traffic_tiles/
    ///
    /// Archive structure mirrors the Valhalla tile hierarchy:
    ///   {level}/{tile_id/1000:D3}/{tile_id%1000:D3}.csv
    ///   e.g.  1/047/701.csv  for tile_id 47701 at hierarchy level 1
    ///
    /// Each CSV row: edge_id,freeflow_speed,constrained_speed
    /// </summary>
    [HttpGet("predicted/tar")]
    [Produces("application/x-tar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPredictedTar(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded for data export." });

        Response.ContentType = "application/x-tar";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"traffic.tar\"";

        await aggregation.WriteTrafficTarAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Get recent speed observations within a geographic bounding box for live traffic display.
    /// Returns GPS-level speed points; use with Valhalla's live traffic overlay.
    /// Maximum bounding box: 1° × 1° (~111 km × 111 km). Maximum age: 60 minutes.
    /// </summary>
    [HttpGet("live")]
    [ProducesResponseType(typeof(LiveTrafficResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveSpeeds([FromQuery] LiveTrafficQuery query, CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        if (query.MinLat >= query.MaxLat || query.MinLon >= query.MaxLon)
            return BadRequest(new { error = "Invalid bounding box: min values must be less than max values." });

        if (Math.Abs(query.MaxLat - query.MinLat) > 1.0 || Math.Abs(query.MaxLon - query.MinLon) > 1.0)
            return BadRequest(new { error = "Bounding box too large. Maximum size is 1° × 1°." });

        var result = await aggregation.GetLiveSpeedsAsync(query, ct);
        return Ok(result);
    }
}
