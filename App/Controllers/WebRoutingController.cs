using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;

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
public class WebRoutingController(ValhallaRoutingService routing) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Route(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "Request body must be a JSON object." });

        var (result, statusCode, error) = await routing.RouteWithTrafficAsync(body, ct);

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
