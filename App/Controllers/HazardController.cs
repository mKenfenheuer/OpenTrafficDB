using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.App.Services;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Report and query traffic hazards (cameras, accidents, police, road works, etc.).
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

    /// <summary>Max distance in meters from a reported point to the nearest OSM edge for a match.</summary>
    private double MaxMatchDistanceMeters =>
        double.TryParse(config["Hazards:MaxMatchDistanceMeters"], out var d) ? d : 50.0;

    /// <summary>Submit a single hazard observation. Map-matching to OSM edges is handled server-side.</summary>
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

        var match = await FindNearestWayAsync(obs.Latitude, obs.Longitude, ct);
        if (match != null)
        {
            obs.OsmWayId = match.Value.OsmWayId;
            obs.IsProcessed = true;

            await UpsertAggregateAsync(
                match.Value.OsmWayId, obs.HazardType,
                match.Value.SnapLat, match.Value.SnapLon,
                obs.Metadata, ct);
        }

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
        var valid = new List<HazardObservationRequest>(request.Observations.Count);
        foreach (var item in request.Observations)
        {
            var validation = validator.Validate(item);
            if (!validation.IsValid)
            {
                response.Rejected++;
                response.Errors.AddRange(validation.Errors);
            }
            else
            {
                valid.Add(item);
                response.Accepted++;
            }
        }

        if (response.Accepted > 0)
        {
            await InsertAndAggregateAsync(valid, keyId, ct);

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
    /// Get all currently active hazards. Supports optional bbox filtering.
    /// This endpoint is public — no authentication required.
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

    private async Task InsertAndAggregateAsync(
        List<HazardObservationRequest> observations, int apiKeyId, CancellationToken ct)
    {
        foreach (var req in observations)
        {
            var obs = MapToObservation(req, apiKeyId);
            var match = await FindNearestWayAsync(obs.Latitude, obs.Longitude, ct);
            if (match != null)
            {
                obs.OsmWayId = match.Value.OsmWayId;
                obs.IsProcessed = true;

                await UpsertAggregateAsync(
                    match.Value.OsmWayId, obs.HazardType,
                    match.Value.SnapLat, match.Value.SnapLon,
                    obs.Metadata, ct);
            }

            db.HazardObservations.Add(obs);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Find the nearest OSM way within MaxMatchDistanceMeters using PostGIS KNN.
    /// </summary>
    private async Task<NearestWayResult?> FindNearestWayAsync(
        double lat, double lon, CancellationToken ct)
    {
        var maxDistMeters = MaxMatchDistanceMeters;
        const double bboxExpand = 0.005;

        var result = await db.Database.SqlQuery<NearestWayRow>($"""
            SELECT
                wg."OsmWayId"                                                            AS "OsmWayId",
                ST_Y(ST_ClosestPoint(wg."Geometry", ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326))) AS "SnapLat",
                ST_X(ST_ClosestPoint(wg."Geometry", ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326))) AS "SnapLon",
                ST_Distance(
                    wg."Geometry"::geography,
                    ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326)::geography
                )                                                                        AS "DistanceMeters"
            FROM "WayGeometries" wg
            WHERE wg."BBox" && ST_Expand(ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326), {bboxExpand})
            ORDER BY wg."Geometry" <-> ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326)
            LIMIT 1
            """)
            .FirstOrDefaultAsync(ct);

        if (result == null || result.DistanceMeters > maxDistMeters)
            return null;

        return new NearestWayResult(result.OsmWayId, result.SnapLat, result.SnapLon);
    }

    private async Task UpsertAggregateAsync(
        long osmWayId, HazardType hazardType, double lat, double lon,
        string? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now + hazardType.GetDefaultExpiry();
        var typeStr = hazardType.ToString();

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "HazardAggregates"
                ("OsmWayId", "HazardType", "Latitude", "Longitude", "Metadata",
                 "ConfirmationCount", "FirstReportedAt", "LastConfirmedAt", "ExpiresAt")
            VALUES (@way_id, @hazard_type, @lat, @lon, @metadata,
                    1, @now, @now, @expires_at)
            ON CONFLICT ("OsmWayId", "HazardType") DO UPDATE SET
                "ConfirmationCount" = "HazardAggregates"."ConfirmationCount" + 1,
                "LastConfirmedAt"   = @now,
                "ExpiresAt"         = @expires_at,
                "Metadata"          = CASE
                    WHEN @metadata IS NULL THEN "HazardAggregates"."Metadata"
                    WHEN "HazardAggregates"."Metadata" IS NULL THEN @metadata
                    ELSE "HazardAggregates"."Metadata" || @metadata
                END,
                "Latitude"          = @lat,
                "Longitude"         = @lon
            """,
            [
                new NpgsqlParameter("way_id",      osmWayId),
                new NpgsqlParameter("hazard_type", typeStr),
                new NpgsqlParameter("lat",         lat),
                new NpgsqlParameter("lon",         lon),
                new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = (object?)metadata ?? DBNull.Value },
                new NpgsqlParameter("now",         now),
                new NpgsqlParameter("expires_at",  expiresAt),
            ], ct);
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
            Latitude           = req.Latitude,
            Longitude          = req.Longitude,
            GpsAccuracyMeters  = req.GpsAccuracyMeters,
            Heading            = req.Heading,
            HazardType         = req.HazardType,
            Metadata           = req.Metadata,
            CreatedAt          = DateTime.UtcNow
        };

    private string GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private (int userId, int keyId) GetUserAndKeyId() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));

    private record struct NearestWayResult(long OsmWayId, double SnapLat, double SnapLon);

    private class NearestWayRow
    {
        public long OsmWayId { get; set; }
        public double SnapLat { get; set; }
        public double SnapLon { get; set; }
        public double DistanceMeters { get; set; }
    }
}
