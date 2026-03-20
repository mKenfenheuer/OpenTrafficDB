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
    /// Exports live traffic speeds as a simple way_id,speed_kmh CSV.
    /// Suitable for feeding into a tool that builds Valhalla traffic.tar files.
    /// Only includes ways with data from the last 60 minutes.
    /// </summary>
    [HttpGet("live/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveTrafficCsv(CancellationToken ct)
    {
        if (!CheckReadRate()) return StatusCode(429, new { error = "Rate limit exceeded." });
        await workers.ProxyAsync("Aggregate", HttpMethod.Get, "/internal/traffic/live/csv",
            GetUserId(), GetKeyId(), string.Empty, null, Response, ct: ct);
        return new EmptyResult();
    }

    private string GetIp()    => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private string GetKeyId() => User.FindFirstValue("keyId") ?? string.Empty;
    private int    GetUserId() => int.Parse(User.FindFirstValue("sub") ?? "0");
    private bool   CheckReadRate() => abuseDetection.CheckReadRate(GetIp(), GetKeyId());
}
