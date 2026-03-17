namespace OpenTrafficDB.Shared.Models;

public class WaySpeedSummary
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public double FreeflowSpeed { get; set; }
    public double? ConstrainedSpeed { get; set; }
    public double P50Speed { get; set; }
    public double P95Speed { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public int TotalSamples { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
