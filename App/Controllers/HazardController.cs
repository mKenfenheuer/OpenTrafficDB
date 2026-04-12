using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Report and query traffic hazards (cameras, accidents, police, road works, etc.).
/// </summary>
[ApiController]
[Route("api/v1/hazards")]
[Produces("application/json")]
public class HazardController(
    AbuseDetectionService abuseDetection,
    CryptoService crypto,
    WorkerClient workers) : ControllerBase
{
    /// <summary>Submit a single hazard observation. Map-matching to OSM edges is handled server-side.</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType(typeof(SubmitHazardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] HazardObservationRequest request, CancellationToken ct)
    {
        var ip = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        await workers.ProxyAsync("Ingest", HttpMethod.Post, "/internal/hazards",
            userId, keyId.ToString(), crypto.HashIp(ip),
            JsonContent.Create(request), Response, ct: ct);

        return new EmptyResult();
    }

    /// <summary>
    /// Submit up to 10,000 hazard observations in a single batch.
    /// Invalid observations are rejected individually; valid ones are still accepted.
    /// </summary>
    [HttpPost("batch")]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType(typeof(BatchHazardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchHazardRequest request, CancellationToken ct)
    {
        var ip = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        await workers.ProxyAsync("Ingest", HttpMethod.Post, "/internal/hazards/batch",
            userId, keyId.ToString(), crypto.HashIp(ip),
            JsonContent.Create(request), Response, ct: ct);

        return new EmptyResult();
    }

    /// <summary>
    /// Get all currently active hazards. Supports optional bbox filtering.
    /// This endpoint is public — no authentication required.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<ActiveHazardDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActive(
        [FromQuery] double? minLat, [FromQuery] double? minLon,
        [FromQuery] double? maxLat, [FromQuery] double? maxLon,
        CancellationToken ct)
    {
        var query = "";
        if (minLat.HasValue && minLon.HasValue && maxLat.HasValue && maxLon.HasValue)
            query = $"?minLat={minLat}&minLon={minLon}&maxLat={maxLat}&maxLon={maxLon}";

        await workers.ProxyPublicAsync("Aggregate",
            "/internal/hazards/active" + query, Response, ct);
        return new EmptyResult();
    }

    private string GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private (int userId, int keyId) GetUserAndKeyId() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));
}
