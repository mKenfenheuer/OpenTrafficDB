using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Endpoints for consuming aggregated traffic data.
/// </summary>
[ApiController]
[Route("api/v1/traffic")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class TrafficController(
    AbuseDetectionService abuseDetection,
    WorkerClient workers) : ControllerBase
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
        var ip    = GetIp();
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        if (query.MinLat >= query.MaxLat || query.MinLon >= query.MaxLon)
            return BadRequest(new { error = "Invalid bounding box: min values must be less than max values." });

        if (Math.Abs(query.MaxLat - query.MinLat) > 1.0 || Math.Abs(query.MaxLon - query.MinLon) > 1.0)
            return BadRequest(new { error = "Bounding box too large. Maximum size is 1° × 1°." });

        var qs = $"?minLat={query.MinLat}&maxLat={query.MaxLat}&minLon={query.MinLon}&maxLon={query.MaxLon}";
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/live",
            GetUserId(), keyId, string.Empty, null, Response, query: qs, ct: ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Current per-way traffic as CSV.
    /// </summary>
    [HttpGet("current/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetCurrentWayCsv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/current/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Live way-level traffic statistics as CSV (rolling 30-minute window).
    /// </summary>
    [HttpGet("live/way/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveWayCsv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/live/way/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Historic way traffic as CSV from pre-aggregated 5-minute buckets (full week).
    /// </summary>
    [HttpGet("historic/way/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetHistoricWayCsv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/historic/way/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Historic H3-grid traffic CSV from pre-aggregated 5-minute buckets (full week).
    /// </summary>
    [HttpGet("historic/h3/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetHistoricH3Csv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/historic/h3/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Exports traffic data in Valhalla predicted-traffic CSV format.
    /// </summary>
    [HttpGet("valhalla/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetValhallaExportCsv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/valhalla/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Live H3-grid traffic statistics as CSV (rolling 30-minute window, resolution 9).
    /// </summary>
    [HttpGet("live/h3/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveH3Csv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/live/h3/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    private string GetIp()    => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private string GetKeyId() => User.FindFirstValue("keyId") ?? string.Empty;
    private int    GetUserId() => int.Parse(User.FindFirstValue("sub") ?? "0");
    private bool   CheckReadRate() => abuseDetection.CheckReadRate(GetIp(), GetKeyId());
}
