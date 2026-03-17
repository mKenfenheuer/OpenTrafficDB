namespace OpenTrafficDB.Shared.Models;

public class WaySpeedAggregate
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public int DayOfWeek { get; set; }
    public int FiveMinuteBucket { get; set; }
    public double SumSpeedKmh { get; set; }
    public int SampleCount { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
}
