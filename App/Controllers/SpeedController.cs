using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>Submit GPS speed observations to contribute to traffic data.</summary>
[ApiController]
[Route("api/v1/speed")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class SpeedController(
    AbuseDetectionService abuseDetection,
    CryptoService crypto,
    WorkerClient workers) : ControllerBase
{
    /// <summary>Submit a single speed observation. Map-matching to OSM ways is handled server-side.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitSpeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] SpeedObservationRequest request, CancellationToken ct)
    {
        var ip    = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        await workers.ProxyAsync("Ingest", HttpMethod.Post, "/internal/speed",
            userId, keyId.ToString(), crypto.HashIp(ip),
            JsonContent.Create(request), Response, ct: ct);

        return new EmptyResult();
    }

    /// <summary>
    /// Submit up to 100 speed observations in a single batch.
    /// Invalid observations in a batch are rejected individually; valid ones are still accepted.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(BatchSubmitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchSpeedRequest request, CancellationToken ct)
    {
        var ip    = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        await workers.ProxyAsync("Ingest", HttpMethod.Post, "/internal/speed/batch",
            userId, keyId.ToString(), crypto.HashIp(ip),
            JsonContent.Create(request), Response, ct: ct);

        return new EmptyResult();
    }

    private string GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private (int userId, int keyId) GetUserAndKeyId() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));
}
