using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.RoutingWorker.Services;
using OpenTrafficDB.Shared.Models.DTOs;

namespace OpenTrafficDB.RoutingWorker.Controllers;

[ApiController]
[Route("internal/route")]
[Authorize]
public class RoutingInternalController(
    RoutingService routing,
    ILogger<RoutingInternalController> logger) : ControllerBase
{
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> Route(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        JsonElement? body,
        [FromQuery] string? json,
        [FromQuery] bool raw = false,
        CancellationToken ct = default)
    {
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
            return BadRequest(new { error = "Provide the route request as a POST body or as the 'json' query parameter." });
        }

        if (raw)
        {
            var (result, statusCode, error) = await routing.RouteRawAsync(request, ct);
            if (result is null)
                return StatusCode(statusCode, new { error });
            return Ok(result);
        }

        // Traffic-enriched route — fall back to raw on failure so the user always gets a route
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

            // Signal to the caller that traffic enrichment failed
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
