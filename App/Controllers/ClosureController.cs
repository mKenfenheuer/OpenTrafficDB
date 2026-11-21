using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Report closed road sections as polylines. Each line is map-matched as a whole, and the roads
/// it covers are closed for routing (and drawn hatched in the traffic tiles) until the closure
/// expires without being reported again.
/// </summary>
[ApiController]
[Route("api/v1/closures")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class ClosureController(AbuseDetectionService abuseDetection, ClosureService closures) : ControllerBase
{
    /// <summary>Submit up to 2,000 closed road sections.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ClosureBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] ClosureBatchRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirst("keyId")?.Value ?? "";
        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        return Ok(await closures.SubmitAsync(request, ct));
    }
}
