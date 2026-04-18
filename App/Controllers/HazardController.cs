using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Services;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Report and query traffic hazards (cameras, accidents, police, road works, etc.).
///
/// Reports are persisted raw on submission; clustering, heading averaging and map-matching
/// happen out-of-band in <see cref="HazardAggregationService"/>.
/// </summary>
[ApiController]
[Route("api/v1/hazards")]
[Produces("application/json")]
public class HazardController(
    AbuseDetectionService abuseDetection,
    AppDbContext db,
    HazardTileService tileService,
    HazardValidationService validator,
    IConfiguration config) : ControllerBase
{
    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    /// <summary>Submit a single hazard observation. Map-matching happens asynchronously.</summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType(typeof(SubmitHazardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] HazardObservationRequest request, CancellationToken ct)
    {
        var ip = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors });

        var obs = MapToObservation(request, keyId);
        db.HazardObservations.Add(obs);

        var user = await db.Users.FindAsync([userId], ct);
        if (user != null)
        {
            user.TotalContributions++;
            TryPromoteToContributor(user);
        }

        await db.SaveChangesAsync(ct);

        return Ok(new SubmitHazardResponse
        {
            Success = true,
            ObservationId = obs.Id,
            Warning = validation.Warning
        });
    }

    /// <summary>
    /// Submit up to 10,000 hazard observations in a single batch.
    /// Invalid observations are rejected individually; valid ones are still accepted.
    /// </summary>
    [HttpPost("batch")]
    [Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
    [ProducesResponseType(typeof(BatchHazardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchHazardRequest request, CancellationToken ct)
    {
        var ip = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        var response = new BatchHazardResponse();
        foreach (var item in request.Observations)
        {
            var validation = validator.Validate(item);
            if (!validation.IsValid)
            {
                response.Rejected++;
                response.Errors.AddRange(validation.Errors);
                continue;
            }

            db.HazardObservations.Add(MapToObservation(item, keyId));
            response.Accepted++;
        }

        if (response.Accepted > 0)
        {
            var user = await db.Users.FindAsync([userId], ct);
            if (user != null)
            {
                user.TotalContributions += response.Accepted;
                TryPromoteToContributor(user);
            }
            await db.SaveChangesAsync(ct);
        }

        return Ok(response);
    }

    /// <summary>
    /// Get all currently active (non-expired) hazards as CSV.
    /// Public — no authentication required.
    /// </summary>
    [HttpGet("live/csv")]
    [AllowAnonymous]
    [Produces("text/csv")]
    public async Task<IActionResult> GetActive(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_hazards.csv\"";
        await tileService.WriteHazardsLiveCsv(Response.Body, ct);
        return new EmptyResult();
    }

    private void TryPromoteToContributor(User user)
    {
        if (user.Role == UserRole.User && user.TotalContributions >= ContributorThreshold)
            user.Role = UserRole.Contributor;
    }

    private static HazardObservation MapToObservation(HazardObservationRequest req, int apiKeyId) =>
        new()
        {
            ApiKeyId          = apiKeyId,
            Timestamp         = req.Timestamp.ToUniversalTime(),
            Latitude          = req.Latitude,
            Longitude         = req.Longitude,
            GpsAccuracyMeters = req.GpsAccuracyMeters,
            Heading           = req.Heading,
            HazardType        = req.HazardType,
            Metadata          = req.Metadata,
            CreatedAt         = DateTime.UtcNow
        };

    private string GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private (int userId, int keyId) GetUserAndKeyId() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));
}
