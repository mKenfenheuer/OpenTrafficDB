using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

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
public class WebRoutingController(WorkerClient workers) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Route(
        [FromBody] JsonElement body,
        [FromQuery] bool raw = false,
        CancellationToken ct = default)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Request body must be a JSON object." });

        var query = raw ? "?raw=true" : null;

        await workers.ProxyAsync("Routing", HttpMethod.Post, "/internal/route",
            0, string.Empty, string.Empty,
            new System.Net.Http.StringContent(body.GetRawText(), System.Text.Encoding.UTF8, "application/json"),
            Response, query: query, ct: ct);

        return new EmptyResult();
    }

    [HttpPost("buckets")]
    public async Task<IActionResult> TravelTimeBuckets(
        [FromBody] TravelTimeBucketRequest request,
        CancellationToken ct = default)
    {
        var bodyJson = JsonSerializer.Serialize(request);
        await workers.ProxyAsync("Routing", HttpMethod.Post, "/internal/route/buckets",
            0, string.Empty, string.Empty,
            new System.Net.Http.StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
            Response, ct: ct);

        return new EmptyResult();
    }
}
