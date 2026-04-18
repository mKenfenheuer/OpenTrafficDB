using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Traffic-enriched routing.
/// </summary>
[ApiController]
[Route("api/v1/route")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class RoutingController(
    AbuseDetectionService abuseDetection,
    RoutingService routing,
    ILogger<RoutingController> logger) : ControllerBase
{
    /// <summary>
    /// Route with traffic enrichment.
    /// Accepts a route request as POST body or GET ?json= parameter.
    /// </summary>
    [HttpGet]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Route(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        JsonElement? body,
        [FromQuery] string? json,
        [FromQuery] bool raw = false,
        CancellationToken ct = default)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        JsonElement request;
        if (Request.Method == HttpMethods.Post &&
            body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
            request = body.Value;
        }
        else if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonDocument.Parse(json).RootElement;
                if (parsed.ValueKind != JsonValueKind.Object)
                    return BadRequest(new { error = "The 'json' query parameter must be a JSON object." });
                request = parsed;
            }
            catch (JsonException)
            {
                return BadRequest(new { error = "The 'json' query parameter is not valid JSON." });
            }
        }
        else
        {
            return BadRequest(new { error = "Provide the route request as a POST body or as the 'json' query parameter." });
        }

        return await RouteInternal(request, raw, ct);
    }

    /// <summary>
    /// Get predicted travel times for each time-of-day bucket across a full day.
    /// </summary>
    [HttpPost("buckets")]
    [ProducesResponseType(typeof(TravelTimeBucketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TravelTimeBuckets(
        [FromBody] TravelTimeBucketRequest request,
        CancellationToken ct = default)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        var (result, statusCode, error) = await routing.RouteTravelTimeBucketsAsync(request, ct);
        if (result is null)
            return StatusCode(statusCode, new { error });
        return Ok(result);
    }

    private async Task<IActionResult> RouteInternal(JsonElement request, bool raw, CancellationToken ct)
    {
        if (raw)
        {
            var (result, statusCode, error) = await routing.RouteRawAsync(request, ct);
            if (result is null)
                return StatusCode(statusCode, new { error });
            return Ok(result);
        }

        try
        {
            var (result, statusCode, error) = await routing.RouteWithTrafficAsync(request, ct);
            if (result is null)
                return StatusCode(statusCode, new { error });
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traffic enrichment failed, falling back to raw route.");

            var (fallback, statusCode, error) = await routing.RouteRawAsync(request, ct);
            if (fallback is null)
                return StatusCode(statusCode, new { error });

            Response.Headers["X-Traffic-Enrichment"] = "failed";
            return Ok(fallback);
        }
    }
}
