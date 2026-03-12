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
    /// Current per-way traffic as CSV: live observations (last 30 min) take priority;
    /// ways without live data fall back to the historic baseline for the matching
    /// day-of-week / 5-minute time slot, giving full road-network coverage.
    /// Format: osm_way_id,speed_kmh,speed_limit_kmh,delay_factor,jam_estimate,source,live_samples,historic_samples
    /// </summary>
    [HttpGet("current/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetCurrentWayCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"current_way_traffic.csv\"";

        await aggregation.WriteCurrentWayCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Live way-level traffic statistics as CSV (rolling 30-minute window).
    /// Aggregates observations by OSM way ID.
    /// Format: osm_way_id,avg_speed_kmh,sample_count
    /// </summary>
    [HttpGet("live/way/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveWayCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_way_traffic.csv\"";

        await aggregation.WriteLiveWayCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Historic way traffic as CSV from pre-aggregated 5-minute buckets (full week).
    /// One row per (way, time_slot) — only slots with observed data are emitted.
    /// time = day_of_week * 288 + minute_of_day / 5, range 0–2015 (Sunday midnight = 0).
    /// Format: osm_way_id,speed,time
    ///   speed in km/h.
    /// </summary>
    [HttpGet("historic/way/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetHistoricWayCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"historic_way_traffic.csv\"";

        await aggregation.WriteHistoricWayCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Historic H3-grid traffic CSV from pre-aggregated 5-minute buckets (full week).
    /// One row per (h3_index, time_slot). time = day_of_week * 288 + minute_of_day / 5, range 0–2015.
    /// Format: h3_index,speed,time
    ///   h3_index: H3 cell id as lowercase hex string; speed in km/h.
    /// </summary>
    [HttpGet("historic/h3/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetHistoricH3Csv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"historic_h3_traffic.csv\"";

        await aggregation.WriteHistoricH3CsvAsync(Response.Body, ct);
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
