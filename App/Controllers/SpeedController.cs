using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.App.Pipeline;
using OpenTrafficDB.App.Services;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDB.App.Controllers;

/// <summary>Submit GPS speed observations to contribute to traffic data.</summary>
[ApiController]
[Route("api/v1/speed")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class SpeedController(
    AbuseDetectionService abuseDetection,
    CryptoService crypto,
    AppDbContext db,
    SpeedValidationService validator,
    SpeedPipeline pipeline,
    IConfiguration config) : ControllerBase
{
    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    /// <summary>Submit a single speed observation. Map-matching to OSM ways is handled server-side.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitSpeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit([FromBody] SpeedObservationRequest request, CancellationToken ct)
    {
        var ip = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors });

        if (!Enqueue([request], keyId))
            return PipelineFull();

        var user = await db.Users.FindAsync([userId], ct);
        if (user != null)
        {
            user.TotalContributions++;
            TryPromoteToContributor(user);
        }

        await db.SaveChangesAsync(ct);

        return Ok(new SubmitSpeedResponse
        {
            Success = true,
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
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchSpeedRequest request, CancellationToken ct)
    {
        var ip = GetIp();
        var (userId, keyId) = GetUserAndKeyId();

        if (!await abuseDetection.CheckSubmitRateAsync(ip, keyId.ToString()))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        var response = new BatchSubmitResponse();
        var valid = new List<SpeedObservationRequest>(request.Observations.Count);
        foreach (var item in request.Observations)
        {
            var validation = validator.Validate(item);
            if (!validation.IsValid)
            {
                response.Rejected++;
                response.Errors.AddRange(validation.Errors.Select(e => $"[trip {item.TripId}]: {e}"));
            }
            else
            {
                valid.Add(item);
                response.Accepted++;
            }
        }

        if (response.Accepted > 0)
        {
            if (!Enqueue(valid, keyId))
                return PipelineFull();

            var user = await db.Users.FindAsync([userId], ct);
            if (user != null)
            {
                user.TotalContributions += response.Accepted;
                TryPromoteToContributor(user);
                await db.SaveChangesAsync(ct);
            }
        }

        return Ok(response);
    }

    /// <summary>
    /// Hands the observations to the in-memory speed pipeline, grouped by trip. Returns false
    /// (and accepts nothing) when the pipeline buffer is full.
    /// </summary>
    private bool Enqueue(List<SpeedObservationRequest> observations, int apiKeyId)
    {
        if (!pipeline.HasCapacity(observations.Count)) return false;

        foreach (var trip in observations.GroupBy(o => o.TripId))
        {
            var points = trip
                .Select(o => (o.Timestamp.ToUniversalTime(), o.Latitude, o.Longitude, o.SpeedMs, o.GpsAccuracyMeters))
                .ToList();
            // Trip ids are namespaced per API key and hashed, as before.
            if (!pipeline.TryEnqueue(crypto.HashTripId(apiKeyId + trip.Key), points))
                return false;
        }
        return true;
    }

    private ObjectResult PipelineFull()
    {
        Response.Headers.RetryAfter = "10";
        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "Processing queue is full. Retry after 10 seconds." });
    }

    private void TryPromoteToContributor(User user)
    {
        if (user.Role == UserRole.User && user.TotalContributions >= ContributorThreshold)
            user.Role = UserRole.Contributor;
    }

    private string GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private (int userId, int keyId) GetUserAndKeyId() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));
}
