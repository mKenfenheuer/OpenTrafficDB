using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;
using OpenTrafficDBModels.DTOs;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Traffic-enriched routing for cookie-authenticated web sessions.
/// Hidden from Swagger — intended for the /map/routing UI only.
/// </summary>
[ApiController]
[Route("web/route")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
[Produces("application/json")]
[ApiExplorerSettings(IgnoreApi = true)]
public class WebRoutingController(
    RoutingService routing,
    ILogger<WebRoutingController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Route(
        [FromBody] JsonElement body,
        [FromQuery] bool raw = false,
        CancellationToken ct = default)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Request body must be a JSON object." });

        if (raw)
        {
            var (result, statusCode, error) = await routing.RouteRawAsync(body, ct);
            if (result is null)
                return StatusCode(statusCode, new { error });
            return Ok(result);
        }

        try
        {
            var (result, statusCode, error) = await routing.RouteWithTrafficAsync(body, ct);
            if (result is null)
                return StatusCode(statusCode, new { error });
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traffic enrichment failed, falling back to raw route.");
            var (fallback, statusCode, error) = await routing.RouteRawAsync(body, ct);
            if (fallback is null)
                return StatusCode(statusCode, new { error });
            Response.Headers["X-Traffic-Enrichment"] = "failed";
            return Ok(fallback);
        }
    }

    [HttpPost("buckets")]
    public async Task<IActionResult> TravelTimeBuckets(
        [FromBody] TravelTimeBucketRequest request,
        CancellationToken ct = default)
    {
        var (result, statusCode, error) = await routing.RouteTravelTimeBucketsAsync(request, ct);
        if (result is null)
            return StatusCode(statusCode, new { error });
        return Ok(result);
    }
}
