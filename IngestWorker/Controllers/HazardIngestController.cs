using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.IngestWorker.Services;

namespace OpenTrafficDB.IngestWorker.Controllers;

[ApiController]
[Route("internal/hazards")]
[Authorize]
public class HazardIngestController(
    AppDbContext db,
    HazardValidationService validator,
    IConfiguration config,
    ILogger<HazardIngestController> logger) : ControllerBase
{
    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    /// <summary>Max distance in meters from a reported point to the nearest OSM edge for a match.</summary>
    private double MaxMatchDistanceMeters =>
        double.TryParse(config["Hazards:MaxMatchDistanceMeters"], out var d) ? d : 50.0;

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] HazardObservationRequest request, CancellationToken ct)
    {
        var (userId, keyId) = GetContext();

        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors });

        var obs = MapToObservation(request, keyId);

        // Map-match: find nearest OSM edge via PostGIS KNN
        var match = await FindNearestWayAsync(obs.Latitude, obs.Longitude, ct);
        if (match != null)
        {
            obs.OsmWayId = match.Value.OsmWayId;
            obs.IsProcessed = true;

            // Upsert aggregate
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

    [HttpPost("batch")]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchHazardRequest request, CancellationToken ct)
    {
        var (userId, keyId) = GetContext();
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

    private async Task InsertAndAggregateAsync(
        List<HazardObservationRequest> observations, int apiKeyId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

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
    /// Returns the OsmWayId and the snapped (closest) point on the edge geometry.
    /// </summary>
    private async Task<NearestWayResult?> FindNearestWayAsync(
        double lat, double lon, CancellationToken ct)
    {
        var maxDistMeters = MaxMatchDistanceMeters;
        // ~0.005 degrees ≈ 500m buffer for bbox pre-filter
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

    /// <summary>
    /// Upserts a HazardAggregate row: increment confirmation count, update expiry and metadata.
    /// Uses PostgreSQL ON CONFLICT for atomic upsert.
    /// </summary>
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

    private (int userId, int keyId) GetContext() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"));

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

    private record struct NearestWayResult(long OsmWayId, double SnapLat, double SnapLon);

    // EF SqlQuery mapping row
    private class NearestWayRow
    {
        public long OsmWayId { get; set; }
        public double SnapLat { get; set; }
        public double SnapLon { get; set; }
        public double DistanceMeters { get; set; }
    }
}
