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
    /// Export predicted traffic speeds in Valhalla CSV format.
    /// Use the output with: valhalla_add_predicted_traffic --config valhalla.json predicted_traffic.csv
    ///
    /// Format: edgeid,speed_kmh_bucket_0,...,speed_kmh_bucket_2015
    /// 2016 buckets = 7 days × 288 five-minute slots per day
    /// Bucket index = dayOfWeek * 288 + minuteOfDay / 5 (dayOfWeek: 0=Sunday)
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
