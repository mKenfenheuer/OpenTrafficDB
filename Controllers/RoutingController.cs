using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>
/// Traffic-enriched routing: proxies Valhalla /route requests and overlays
/// per-route delay and congestion analysis from the OpenTrafficDB speed data.
/// </summary>
[ApiController]
[Route("api/v1/route")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class RoutingController(
    ValhallaRoutingService routing,
    AbuseDetectionService abuseDetection) : ControllerBase
{
    /// <summary>
    /// Route with traffic enrichment.
    ///
    /// Accepts a standard Valhalla /route request as either:
    /// - POST with a JSON body, or
    /// - GET with the request serialised as the <c>json</c> query parameter.
    ///
    /// <c>alternates</c> is automatically raised to at least 10 so the
    /// traffic-optimal route can be selected from a wide candidate pool.
    /// Only the number of routes originally requested are returned, ranked
    /// by ascending live travel time.
    ///
    /// Each <c>trip</c> object in the response contains a <c>trafficAnalysis</c> field:
    /// - <c>freeflowDurationMinutes</c> / <c>liveDurationMinutes</c>
    /// - <c>totalDelayMinutes</c>, <c>delayPercent</c>
    /// - <c>trafficCoverageRatio</c> (0–1, fraction of route with live data)
    /// - <c>jams[]</c> — individual congestion segments with geometry, delay, and speeds
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
        CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        JsonElement request;
        if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
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
            return BadRequest(new { error = "Provide the Valhalla route request as a POST body or as the 'json' query parameter." });
        }

        var (result, statusCode, error) = await routing.RouteWithTrafficAsync(request, ct);

        if (result is null)
        {
            return statusCode switch
            {
                400 => BadRequest(new { error }),
                503 => StatusCode(503, new { error }),
                _   => StatusCode(502, new { error })
            };
        }

        return Ok(result);
    }
}
