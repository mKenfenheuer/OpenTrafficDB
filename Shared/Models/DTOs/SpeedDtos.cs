using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.Shared.Models.DTOs;

public class SpeedObservationRequest
{
    [Required]
    public DateTime Timestamp { get; set; }

    [Required, Range(-90.0, 90.0)]
    public double Latitude { get; set; }

    [Required, Range(-180.0, 180.0)]
    public double Longitude { get; set; }

    [Required, Range(0.0, 200.0)]
    public double SpeedMs { get; set; }

    [Range(0.0, 360.0)]
    public float? Heading { get; set; }

    [Range(-500.0, 15000.0)]
    public float? AltitudeMeters { get; set; }

    [Required, Range(0.01, 10000.0)]
    public float GpsAccuracyMeters { get; set; }

    [Required]
    public string TripId { get; set; } = Guid.NewGuid().ToString();

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
    [Required, MinLength(1), MaxLength(100_000)]
    public List<SpeedObservationRequest> Observations { get; set; } = [];
}

public class BatchSubmitResponse
{
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public List<string> Errors { get; set; } = [];
}
