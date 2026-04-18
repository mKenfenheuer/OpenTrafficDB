namespace OpenTrafficDB.Shared.Models;

/// <summary>
/// A single user-reported hazard observation (raw GPS point before aggregation).
/// </summary>
public class HazardObservation
{
    public long Id { get; set; }
    public int ApiKeyId { get; set; }
    public ApiKey ApiKey { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float GpsAccuracyMeters { get; set; }
    public float Heading { get; set; }
    public HazardType HazardType { get; set; }
    /// <summary>Optional JSON metadata (e.g. {"max_speed":50} for speed cameras).</summary>
    public string? Metadata { get; set; }
    public long? OsmWayId { get; set; }
    public bool IsProcessed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
