namespace OpenTrafficDBModels;

public class WaySpeedSummary
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public double FreeflowSpeed { get; set; }
    public double? ConstrainedSpeed { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    /// <summary>Pre-computed Valhalla DCT-encoded speed string. Null until first aggregation.</summary>
    public string? EncodedSpeeds { get; set; }
}
