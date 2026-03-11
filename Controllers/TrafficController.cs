using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>
/// Endpoints for consuming aggregated traffic data.
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
    /// Get recent speed observations within a geographic bounding box for live traffic display.
    /// Returns GPS-level speed points. Maximum bounding box: 1° × 1°. Maximum age: 60 minutes.
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

        return Ok(await aggregation.GetLiveSpeedsAsync(query, ct));
    }

    /// <summary>
    /// Live edge-level traffic statistics as CSV (rolling 30-minute window).
    /// Aggregates observations by OSM directed edge (way_id + start/end node IDs).
    /// Format: osm_way_id,start_node_id,end_node_id,avg_speed_kmh,sample_count
    /// </summary>
    [HttpGet("live/edge/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveEdgeCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_edge_traffic.csv\"";

        await aggregation.WriteLiveEdgeCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Historic edge traffic as CSV from pre-aggregated 5-minute buckets (full week).
    /// One row per (edge, time_slot) — only slots with observed data are emitted.
    /// time = day_of_week * 288 + minute_of_day / 5, range 0–2015 (Sunday midnight = 0).
    /// Format: osm_way_id,start_node_id,end_node_id,speed,time
    ///   speed in km/h.
    /// </summary>
    [HttpGet("historic/edge/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetHistoricEdgeCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"historic_edge_traffic.csv\"";

        await aggregation.WriteHistoricEdgeCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Live H3-grid traffic statistics as CSV (rolling 30-minute window, resolution 9 ~174 m).
    /// Aggregates observations by H3 hexagonal cell for spatial analytics and heatmaps.
    /// Format: h3_index,avg_speed_kmh,sample_count
    ///   h3_index: H3 cell id as lowercase hex string (e.g. 89283082803ffff)
    /// </summary>
    [HttpGet("live/h3/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveH3Csv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_h3_traffic.csv\"";

        await aggregation.WriteLiveH3CsvAsync(Response.Body, ct);
        return new EmptyResult();
    }
}
