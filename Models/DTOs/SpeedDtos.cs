using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.Models.DTOs;

public class SpeedObservationRequest
{
    /// <summary>GPS timestamp in UTC</summary>
    [Required]
    public DateTime Timestamp { get; set; }

    [Required, Range(-90.0, 90.0)]
    public double Latitude { get; set; }

    [Required, Range(-180.0, 180.0)]
    public double Longitude { get; set; }

    /// <summary>Speed in meters per second</summary>
    [Required, Range(0.0, 200.0)]
    public double SpeedMs { get; set; }

    /// <summary>Heading in degrees (0-360), optional</summary>
    [Range(0.0, 360.0)]
    public float? Heading { get; set; }

    /// <summary>Altitude in meters, optional</summary>
    [Range(-500.0, 15000.0)]
    public float? AltitudeMeters { get; set; }

    /// <summary>GPS horizontal accuracy in meters (lower = better quality)</summary>
    [Required, Range(0.01, 10000.0)]
    public float GpsAccuracyMeters { get; set; }

    /// <summary>Anonymized trip UUID — must not contain user-identifiable information</summary>
    [Required]
    public string TripId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Set to true when road works are present at this location.
    /// The server will report a speed capped at 70% of the speed limit for this way,
    /// unless the current aggregate is already below that threshold.
    /// </summary>
    public bool RoadWorks { get; set; }
}

public class SubmitSpeedResponse
{
    public bool Success { get; set; }
    public long? ObservationId { get; set; }
    public string? Warning { get; set; }
}

public class BatchSpeedRequest
{
    /// <summary>Up to 100,000 observations per batch</summary>
    [Required, MinLength(1), MaxLength(100_000)]
    public List<SpeedObservationRequest> Observations { get; set; } = [];
}

public class BatchSubmitResponse
{
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public List<string> Errors { get; set; } = [];
}
