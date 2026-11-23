namespace OpenTrafficDBModels;

public class WaySpeedSummary
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public double FreeflowSpeed { get; set; }
    public double? ConstrainedSpeed { get; set; }
    public double? SpeedLimitKmh { get; set; }
    /// <summary>Road class for zoom-dependent tile filtering: 1 motorway/trunk, 2 primary, 3 secondary, 4 tertiary, 5 other.</summary>
    public short? RoadClass { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
