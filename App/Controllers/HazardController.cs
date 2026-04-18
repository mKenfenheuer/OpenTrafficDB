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
                obs.Heading, match.Value.StreetName,
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
                    obs.Heading, match.Value.StreetName,
                    obs.Metadata, ct);
            }

            db.HazardObservations.Add(obs);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Find the nearest OSM way within MaxMatchDistanceMeters using PostGIS KNN.
    /// Also returns the way's street name for denormalized storage on the aggregate.
    /// </summary>
    private async Task<NearestWayResult?> FindNearestWayAsync(
        double lat, double lon, CancellationToken ct)
    {
        var maxDistMeters = MaxMatchDistanceMeters;
        const double bboxExpand = 0.005;

        var result = await db.Database.SqlQuery<NearestWayRow>($"""
            SELECT
                wg."OsmWayId"                                                            AS "OsmWayId",
                wg."Name"                                                                AS "StreetName",
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

        return new NearestWayResult(result.OsmWayId, result.SnapLat, result.SnapLon, result.StreetName);
    }

    /// <summary>Number of confirmations in the last 90 days required to mark a hazard as permanent.</summary>
    private const int PermanentConfirmationThreshold = 3;

    private async Task UpsertAggregateAsync(
        long osmWayId, HazardType hazardType, double lat, double lon,
        float? heading, string? streetName, string? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now + hazardType.GetDefaultExpiry();
        var typeStr = hazardType.ToString();

        // Heading is an angular quantity — averaging via sum-of-sin/cos avoids the
        // 359°/1° wrap-around problem. We store the running sums and sample count;
        // the user-visible "Heading" is recomputed atomically on each upsert.
        double sinInc = 0, cosInc = 0;
        int headingInc = 0;
        if (heading.HasValue)
        {
            var rad = heading.Value * Math.PI / 180.0;
            sinInc = Math.Sin(rad);
            cosInc = Math.Cos(rad);
            headingInc = 1;
        }

        // A hazard is "permanent" when it has been confirmed at least
        // PermanentConfirmationThreshold times AND the earliest of those confirmations
        // is within the last 90 days (i.e. sustained recent activity, not a one-off stale entry).
        var permanentSince = now.AddDays(-90);

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO "HazardAggregates"
                ("OsmWayId", "HazardType", "Latitude", "Longitude",
                 "Heading", "HeadingSinSum", "HeadingCosSum", "HeadingSampleCount",
                 "StreetName", "Metadata",
                 "ConfirmationCount", "IsPermanent",
                 "FirstReportedAt", "LastConfirmedAt", "ExpiresAt")
            VALUES (@way_id, @hazard_type, @lat, @lon,
                    @heading_deg, @sin_inc, @cos_inc, @heading_inc,
                    @street_name, @metadata,
                    1, FALSE,
                    @now, @now, @expires_at)
            ON CONFLICT ("OsmWayId", "HazardType") DO UPDATE SET
                "ConfirmationCount"  = "HazardAggregates"."ConfirmationCount" + 1,
                "LastConfirmedAt"    = @now,
                "ExpiresAt"          = @expires_at,
                "HeadingSinSum"      = "HazardAggregates"."HeadingSinSum" + @sin_inc,
                "HeadingCosSum"      = "HazardAggregates"."HeadingCosSum" + @cos_inc,
                "HeadingSampleCount" = "HazardAggregates"."HeadingSampleCount" + @heading_inc,
                "Heading"            = CASE
                    WHEN "HazardAggregates"."HeadingSampleCount" + @heading_inc > 0 THEN
                        (DEGREES(ATAN2(
                            "HazardAggregates"."HeadingSinSum" + @sin_inc,
                            "HazardAggregates"."HeadingCosSum" + @cos_inc
                        )) + 360)::double precision % 360
                    ELSE NULL
                END,
                "StreetName"         = COALESCE(@street_name, "HazardAggregates"."StreetName"),
                "Metadata"           = CASE
                    WHEN @metadata IS NULL THEN "HazardAggregates"."Metadata"
                    WHEN "HazardAggregates"."Metadata" IS NULL THEN @metadata
                    ELSE "HazardAggregates"."Metadata" || @metadata
                END,
                "Latitude"           = @lat,
                "Longitude"          = @lon,
                "IsPermanent"        = (
                    "HazardAggregates"."ConfirmationCount" + 1 >= {PermanentConfirmationThreshold}
                    AND "HazardAggregates"."FirstReportedAt" >= @permanent_since
                )
            """,
            [
                new NpgsqlParameter("way_id",       osmWayId),
                new NpgsqlParameter("hazard_type",  typeStr),
                new NpgsqlParameter("lat",          lat),
                new NpgsqlParameter("lon",          lon),
                new NpgsqlParameter("heading_deg",  (object?)heading ?? DBNull.Value),
                new NpgsqlParameter("sin_inc",      sinInc),
                new NpgsqlParameter("cos_inc",      cosInc),
                new NpgsqlParameter("heading_inc",  headingInc),
                new NpgsqlParameter("street_name",  (object?)streetName ?? DBNull.Value),
                new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = (object?)metadata ?? DBNull.Value },
                new NpgsqlParameter("now",          now),
                new NpgsqlParameter("expires_at",   expiresAt),
                new NpgsqlParameter("permanent_since", permanentSince),
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

    private record struct NearestWayResult(long OsmWayId, double SnapLat, double SnapLon, string? StreetName);

    private class NearestWayRow
    {
        public long OsmWayId { get; set; }
        public string? StreetName { get; set; }
        public double SnapLat { get; set; }
        public double SnapLon { get; set; }
        public double DistanceMeters { get; set; }
    }
}
