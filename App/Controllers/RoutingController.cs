using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Traffic-enriched routing: forwards requests to RoutingWorker.
/// </summary>
[ApiController]
[Route("api/v1/route")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class RoutingController(
    AbuseDetectionService abuseDetection,
    WorkerClient workers) : ControllerBase
{
    /// <summary>
    /// Route with traffic enrichment.
    /// Accepts a standard Valhalla /route request as POST body or GET ?json= parameter.
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
        var userId = int.Parse(User.FindFirstValue("sub") ?? "0");

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        if (Request.Method == HttpMethods.Post &&
            body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
            await workers.ProxyAsync("Routing", HttpMethod.Post, "/internal/route",
                userId, keyId, string.Empty,
                new System.Net.Http.StringContent(body.Value.GetRawText(), System.Text.Encoding.UTF8, "application/json"),
                Response, ct: ct);
        }
        else if (!string.IsNullOrWhiteSpace(json))
        {
            // Validate JSON before forwarding
            try
            {
                var parsed = JsonDocument.Parse(json).RootElement;
                if (parsed.ValueKind != JsonValueKind.Object)
                    return BadRequest(new { error = "The 'json' query parameter must be a JSON object." });
            }
            catch (JsonException)
            {
                return BadRequest(new { error = "The 'json' query parameter is not valid JSON." });
            }

            await workers.ProxyAsync("Routing", HttpMethod.Get, "/internal/route",
                userId, keyId, string.Empty, null, Response,
                query: $"?json={Uri.EscapeDataString(json)}", ct: ct);
        }
        else
        {
            return BadRequest(new { error = "Provide the Valhalla route request as a POST body or as the 'json' query parameter." });
        }

        return new EmptyResult();
    }
}
