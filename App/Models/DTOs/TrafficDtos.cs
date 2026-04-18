using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDBModels.DTOs;

public class LiveTrafficQuery
{
    public double MinLat { get; set; }
    public double MaxLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLon { get; set; }
}

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

public class BlockIpRequest
{
    [Required]
    public string Ip { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public DateTime? ExpiresAt { get; set; }
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
