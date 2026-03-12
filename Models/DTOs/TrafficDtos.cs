using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.Models.DTOs;

public class LiveSpeedPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Speed in m/s</summary>
    public double SpeedMs { get; set; }

    /// <summary>Speed in km/h</summary>
    public double SpeedKmh { get; set; }

    public float? Heading { get; set; }

    /// <summary>Stable OSM way identifier; null until map-matched</summary>
    public long? OsmWayId { get; set; }

    /// <summary>Max allowed speed in km/h for this edge; null if not available.</summary>
    public double? SpeedLimitKmh { get; set; }

    /// <summary>
    /// Fraction of max speed lost to traffic (0 = free flow, 1 = standing).
    /// Null when SpeedLimitKmh is unavailable.
    /// </summary>
    public double? DelayFactor { get; set; }

    /// <summary>
    /// Traffic jam severity estimate (0 = no jam, ~0.7 = stop-and-go, 1 = standing traffic).
    /// Null when SpeedLimitKmh is unavailable.
    /// </summary>
    public double? JamEstimate { get; set; }

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
    public int TotalUsers { get; set; }
    public int UserCount { get; set; }
    public int ContributorCount { get; set; }
    public int AdminCount { get; set; }
    public int ActiveApiKeys { get; set; }
    public int AggregatedWays { get; set; }
    public int BlockedIps { get; set; }
}

public class AdminUserResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public long TotalContributions { get; set; }
    public DateTime CreatedAt { get; set; }
    public int KeyCount { get; set; }
}

public class AdminKeyResponse
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class SetRoleRequest
{
    [Required]
    public string Role { get; set; } = string.Empty;
}
