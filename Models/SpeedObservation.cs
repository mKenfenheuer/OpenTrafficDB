namespace OpenTrafficDB.Models;

public class SpeedObservation
{
    public long Id { get; set; }

    public int ApiKeyId { get; set; }
    public ApiKey ApiKey { get; set; } = null!;

    /// <summary>GPS timestamp (UTC)</summary>
    public DateTime Timestamp { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Speed in meters per second</summary>
    public double SpeedMs { get; set; }

    /// <summary>Heading in degrees 0-360, null if unavailable</summary>
    public float? Heading { get; set; }

    /// <summary>Altitude in meters, null if unavailable</summary>
    public float? AltitudeMeters { get; set; }

    /// <summary>GPS horizontal accuracy in meters (lower = better)</summary>
    public float GpsAccuracyMeters { get; set; }

    /// <summary>Anonymized trip identifier hashed trip id by the user</summary>
    public string TripId { get; set; } = string.Empty;

    /// <summary>Valhalla GraphId (64-bit edge identifier), optional</summary>
    public long? ValhallaEdgeId { get; set; }

    /// <summary>SHA-256 + pepper of contributor IP — never store raw IP</summary>
    public string IpHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
