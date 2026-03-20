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
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.IngestWorker.Controllers;

[ApiController]
[Route("internal/speed")]
[Authorize]
public class IngestController(
    AppDbContext db,
    SpeedValidationService validator,
    CryptoService crypto,
    IConfiguration config) : ControllerBase
{
    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SpeedObservationRequest request, CancellationToken ct)
    {
        var (userId, keyId, _) = GetContext();

        var validation = validator.Validate(request);
        if (!validation.IsValid)
            return BadRequest(new { errors = validation.Errors });

        var obs = MapToObservation(request, keyId);
        db.SpeedObservations.Add(obs);

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
            ObservationId = obs.Id,
            Warning = validation.Warning
        });
    }

    [HttpPost("batch")]
    public async Task<IActionResult> SubmitBatch([FromBody] BatchSpeedRequest request, CancellationToken ct)
    {
        var (userId, keyId, _) = GetContext();
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
            await InsertObservationsBulkAsync(valid, keyId, ct);

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
    /// Single-round-trip batch insert via unnest. Deduplicates on (TripId, Timestamp).
    /// ~10-50x faster than individual EF SaveChanges calls for large batches.
    /// </summary>
    private async Task InsertObservationsBulkAsync(
        List<SpeedObservationRequest> observations, int apiKeyId, CancellationToken ct)
    {
        var now   = DateTime.UtcNow;
        var count = observations.Count;

        var apiKeyIds  = new int     [count];
        var timestamps = new DateTime[count];
        var latitudes  = new double  [count];
        var longitudes = new double  [count];
        var speedMs    = new double  [count];
        var headings   = new float?  [count];
        var altitudes  = new float?  [count];
        var accuracies = new float   [count];
        var tripIds    = new string  [count];
        var roadWorks  = new bool    [count];
        var createdAts = new DateTime[count];

        for (var i = 0; i < count; i++)
        {
            var r          = observations[i];
            apiKeyIds [i]  = apiKeyId;
            timestamps[i]  = r.Timestamp.ToUniversalTime();
            latitudes [i]  = r.Latitude;
            longitudes[i]  = r.Longitude;
            speedMs   [i]  = r.SpeedMs;
            headings  [i]  = r.Heading;
            altitudes [i]  = r.AltitudeMeters;
            accuracies[i]  = r.GpsAccuracyMeters;
            tripIds   [i]  = crypto.HashTripId(apiKeyId + r.TripId);
            roadWorks [i]  = r.RoadWorks;
            createdAts[i]  = now;
        }

        object[] sqlParams =
        [
            new NpgsqlParameter("api_key_ids",     NpgsqlDbType.Array | NpgsqlDbType.Integer)     { Value = apiKeyIds   },
            new NpgsqlParameter("timestamps",      NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = timestamps  },
            new NpgsqlParameter("latitudes",       NpgsqlDbType.Array | NpgsqlDbType.Double)      { Value = latitudes   },
            new NpgsqlParameter("longitudes",      NpgsqlDbType.Array | NpgsqlDbType.Double)      { Value = longitudes  },
            new NpgsqlParameter("speed_ms_vals",   NpgsqlDbType.Array | NpgsqlDbType.Double)      { Value = speedMs     },
            new NpgsqlParameter("headings",        NpgsqlDbType.Array | NpgsqlDbType.Real)        { Value = headings    },
            new NpgsqlParameter("altitudes",       NpgsqlDbType.Array | NpgsqlDbType.Real)        { Value = altitudes   },
            new NpgsqlParameter("accuracies",      NpgsqlDbType.Array | NpgsqlDbType.Real)        { Value = accuracies  },
            new NpgsqlParameter("trip_ids",        NpgsqlDbType.Array | NpgsqlDbType.Text)        { Value = tripIds     },
            new NpgsqlParameter("road_works_vals", NpgsqlDbType.Array | NpgsqlDbType.Boolean)     { Value = roadWorks   },
            new NpgsqlParameter("created_ats",     NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = createdAts  },
        ];
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SpeedObservations"
                ("ApiKeyId","Timestamp","Latitude","Longitude","SpeedMs",
                 "Heading","AltitudeMeters","GpsAccuracyMeters","TripId",
                 "RoadWorks","OsmWayId","SpeedLimitKmh","IsAggregated","CreatedAt")
            SELECT v.api_key_id, v.ts, v.lat, v.lon, v.speed_ms,
                   v.heading, v.altitude, v.accuracy, v.trip_id,
                   v.road_works, NULL::bigint, NULL::double precision, FALSE, v.created_at
            FROM unnest(@api_key_ids,@timestamps,@latitudes,@longitudes,@speed_ms_vals,
                        @headings,@altitudes,@accuracies,@trip_ids,@road_works_vals,@created_ats)
                 AS v(api_key_id,ts,lat,lon,speed_ms,heading,altitude,accuracy,trip_id,road_works,created_at)
            ON CONFLICT ("TripId","Timestamp") DO NOTHING
            """, sqlParams, ct);
    }

    private (int userId, int keyId, string ipHash) GetContext() =>
        (int.Parse(User.FindFirstValue("sub") ?? "0"),
         int.Parse(User.FindFirstValue("keyId") ?? "0"),
         User.FindFirstValue("ipHash") ?? string.Empty);

    private void TryPromoteToContributor(User user)
    {
        if (user.Role == UserRole.User && user.TotalContributions >= ContributorThreshold)
            user.Role = UserRole.Contributor;
    }

    private SpeedObservation MapToObservation(SpeedObservationRequest req, int apiKeyId) =>
        new()
        {
            ApiKeyId          = apiKeyId,
            Timestamp         = req.Timestamp.ToUniversalTime(),
            Latitude          = req.Latitude,
            Longitude         = req.Longitude,
            SpeedMs           = req.SpeedMs,
            Heading           = req.Heading,
            AltitudeMeters    = req.AltitudeMeters,
            GpsAccuracyMeters = req.GpsAccuracyMeters,
            TripId            = crypto.HashTripId(apiKeyId + req.TripId),
            RoadWorks         = req.RoadWorks,
            CreatedAt         = DateTime.UtcNow
        };
}
