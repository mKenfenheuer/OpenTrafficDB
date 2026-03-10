using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>Submit GPS speed observations to contribute to traffic data.</summary>
[ApiController]
[Route("api/v1/speed")]
[Authorize]
[Produces("application/json")]
public class SpeedController(
    AppDbContext db,
    SpeedValidationService validator,
    AbuseDetectionService abuseDetection,
    JwtService jwtService) : ControllerBase
{
    /// <summary>
    /// Submit a single speed observation.
    /// Observations without a Valhalla edge ID are map-matched server-side in the background.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitSpeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] SpeedObservationRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var apiKeyId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";

        if (!await abuseDetection.CheckSubmitRateAsync(ip, apiKeyId))
            return StatusCode(429, new { error = "Rate limit exceeded. Reduce submission frequency." });

        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors });

        var obs = MapToObservation(request, int.Parse(apiKeyId), jwtService.HashIp(ip));
        db.SpeedObservations.Add(obs);

        var key = await db.ApiKeys.FindAsync(int.Parse(apiKeyId));
        if (key != null) key.TotalContributions++;

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
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchSpeedRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var apiKeyId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";

        if (!await abuseDetection.CheckSubmitRateAsync(ip, apiKeyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        var ipHash = jwtService.HashIp(ip);
        var keyIdInt = int.Parse(apiKeyId);
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

            db.SpeedObservations.Add(MapToObservation(item, keyIdInt, ipHash));
            response.Accepted++;
        }

        if (response.Accepted > 0)
        {
            var key = await db.ApiKeys.FindAsync(keyIdInt);
            if (key != null) key.TotalContributions += response.Accepted;
            await db.SaveChangesAsync();
        }

        return Ok(response);
    }

    private static SpeedObservation MapToObservation(SpeedObservationRequest req, int apiKeyId, string ipHash) =>
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
            TripId = req.TripId,
            // ValhallaEdgeId is set server-side by AggregationBackgroundService
            IpHash = ipHash,
            CreatedAt = DateTime.UtcNow
        };
}
