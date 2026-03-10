using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.Models.DTOs;

public class LiveSpeedPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Speed in m/s</summary>
    public double SpeedMs { get; set; }

    /// <summary>Speed in km/h (Valhalla unit)</summary>
    public double SpeedKmh { get; set; }

    public float? Heading { get; set; }
    public long? ValhallaEdgeId { get; set; }
    public DateTime Timestamp { get; set; }
}

public class LiveTrafficResponse
{
    public List<LiveSpeedPoint> Points { get; set; } = [];
    public DateTime AsOf { get; set; } = DateTime.UtcNow;
    public int TotalPoints { get; set; }
}

public class LiveTrafficQuery
{
    [Required, Range(-90.0, 90.0)]
    public double MinLat { get; set; }

    [Required, Range(-90.0, 90.0)]
    public double MaxLat { get; set; }

    [Required, Range(-180.0, 180.0)]
    public double MinLon { get; set; }

    [Required, Range(-180.0, 180.0)]
    public double MaxLon { get; set; }

    /// <summary>Maximum age of observations in minutes (1–60)</summary>
    [Range(1, 60)]
    public int MaxAgeMinutes { get; set; } = 15;
}

public class BlockIpRequest
{
    [Required]
    public string Ip { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Null = permanent block</summary>
    public DateTime? ExpiresAt { get; set; }
}

public class AdminStatsResponse
{
    public long TotalObservations { get; set; }
    public long ObservationsLast24h { get; set; }
    public int ActiveApiKeys { get; set; }
    public int AggregatedEdges { get; set; }
    public int BlockedIps { get; set; }
}
