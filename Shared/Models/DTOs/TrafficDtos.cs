using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.App.Models.DTOs;

public class LiveSpeedPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double SpeedMs { get; set; }
    public double SpeedKmh { get; set; }
    public float? Heading { get; set; }
    public long? OsmWayId { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public double? DelayFactor { get; set; }
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

    [Range(1, 60)]
    public int MaxAgeMinutes { get; set; } = 15;
}

public record WayTrafficData(
    double  SpeedKmh,
    double? SpeedLimitKmh,
    double? DelayFactor,
    double? JamEstimate,
    string  Source,
    int     LiveSamples,
    int     HistoricSamples);

public class BlockIpRequest
{
    [Required]
    public string Ip { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

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
