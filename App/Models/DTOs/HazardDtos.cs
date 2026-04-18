using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using OpenTrafficDB.Shared.Models;

namespace OpenTrafficDB.Shared.Models.DTOs;

public class HazardObservationRequest
{
    [Required]
    public DateTime Timestamp { get; set; }

    [Required, Range(-90.0, 90.0)]
    public double Latitude { get; set; }

    [Required, Range(-180.0, 180.0)]
    public double Longitude { get; set; }

    [Required, Range(0.01, 10000.0)]
    public float GpsAccuracyMeters { get; set; }

    [Required, Range(0.0, 360.0)]
    public float Heading { get; set; }

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HazardType HazardType { get; set; }

    /// <summary>Optional JSON metadata (e.g. {"max_speed": 50} for speed cameras).</summary>
    public string? Metadata { get; set; }
}

public class SubmitHazardResponse
{
    public bool Success { get; set; }
    public long? ObservationId { get; set; }
    public string? Warning { get; set; }
}

public class BatchHazardRequest
{
    [Required, MinLength(1), MaxLength(10_000)]
    public List<HazardObservationRequest> Observations { get; set; } = [];
}

public class BatchHazardResponse
{
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class ActiveHazardDto
{
    public long OsmWayId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HazardType HazardType { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Average travel direction (0–360°) across all reports.</summary>
    public float Heading { get; set; }
    /// <summary>Street name of the matched OSM edge, if known.</summary>
    public string? StreetName { get; set; }
    /// <summary>True if repeatedly reported within the last 90 days.</summary>
    public bool IsPermanent { get; set; }
    public string? Metadata { get; set; }
    public int ConfirmationCount { get; set; }
    public DateTime LastConfirmedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
