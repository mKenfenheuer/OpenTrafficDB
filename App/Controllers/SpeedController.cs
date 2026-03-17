using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.App.Data;
using OpenTrafficDB.App.Models;
using OpenTrafficDB.App.Models.DTOs;
using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>Submit GPS speed observations to contribute to traffic data.</summary>
[ApiController]
[Route("api/v1/speed")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class SpeedController(
    AppDbContext db,
    SpeedValidationService validator,
    CryptoService crypto,
    IConfiguration config) : ControllerBase
{
    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    /// <summary>Submit a single speed observation. Map-matching to OSM ways is handled server-side.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitSpeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromBody] SpeedObservationRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (userId, keyId) = GetUserAndKeyId();

        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors });

        var obs = MapToObservation(request, keyId, crypto.HashIp(ip));
        db.SpeedObservations.Add(obs);

        var user = await db.Users.FindAsync(userId);
        if (user != null)
        {
            user.TotalContributions++;
            TryPromoteToContributor(user);
        }

        await db.SaveChangesAsync();

        return Ok(new SubmitSpeedResponse
        {
            Success = true,
            ObservationId = obs.Id,
            Warning = validation.Warning
        });
    }

    /// <summary>
    /// Submit up to 100 speed observations in a single batch.
    /// Invalid observations in a batch are rejected individually; valid ones are still accepted.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(BatchSubmitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchSpeedRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (userId, keyId) = GetUserAndKeyId();
        var ipHash = crypto.HashIp(ip);
        var response = new BatchSubmitResponse();

        foreach (var item in request.Observations)
        {
            var validation = validator.Validate(item);
            if (!validation.IsValid)
            {
                response.Rejected++;
                response.Errors.AddRange(validation.Errors.Select(e => $"[trip {item.TripId}]: {e}"));
                continue;
            }

            db.SpeedObservations.Add(MapToObservation(item, keyId, ipHash));
            response.Accepted++;
        }

        if (response.Accepted > 0)
        {
            var user = await db.Users.FindAsync(userId);
            if (user != null)
            {
                user.TotalContributions += response.Accepted;
                TryPromoteToContributor(user);
            }
            await db.SaveChangesAsync();
        }

        return Ok(response);
    }

    private (int userId, int keyId) GetUserAndKeyId() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));

    private void TryPromoteToContributor(User user)
    {
        if (user.Role == UserRole.User && user.TotalContributions >= ContributorThreshold)
            user.Role = UserRole.Contributor;
    }

    private SpeedObservation MapToObservation(SpeedObservationRequest req, int apiKeyId, string ipHash) =>
        new()
        {
            ApiKeyId = apiKeyId,
            Timestamp = req.Timestamp.ToUniversalTime(),
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            SpeedMs = req.SpeedMs,
            Heading = req.Heading,
            AltitudeMeters = req.AltitudeMeters,
            GpsAccuracyMeters = req.GpsAccuracyMeters,
            TripId = crypto.HashTripId(apiKeyId + req.TripId),
            RoadWorks = req.RoadWorks,
            IpHash = ipHash,
            CreatedAt = DateTime.UtcNow
        };
}
