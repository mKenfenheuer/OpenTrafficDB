using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.RoutingWorker.Services;

namespace OpenTrafficDB.RoutingWorker.Controllers;

[ApiController]
[Route("internal/route")]
[Authorize]
public class RoutingInternalController(ValhallaRoutingService routing) : ControllerBase
{
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> Route(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        JsonElement? body,
        [FromQuery] string? json,
        CancellationToken ct)
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
