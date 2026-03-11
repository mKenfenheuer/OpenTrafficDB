namespace OpenTrafficDB.Models;

/// <summary>
/// Pre-aggregated speed data per OSM directed edge per 5-minute time bucket.
///
/// Stable key: (OsmWayId, OsmStartNodeId, OsmEndNodeId, DayOfWeek, FiveMinuteBucket).
/// The start/end OSM node IDs uniquely identify the directed segment and remain
/// stable across Valhalla tile rebuilds.
/// </summary>
public class EdgeSpeedAggregate
{
    public long Id { get; set; }

    /// <summary>Stable OSM way identifier</summary>
    public long OsmWayId { get; set; }

    /// <summary>OSM node ID at the start of this directed edge</summary>
    public long OsmStartNodeId { get; set; }

    /// <summary>OSM node ID at the end of this directed edge</summary>
    public long OsmEndNodeId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (matches C# DayOfWeek)</summary>
    public int DayOfWeek { get; set; }

    /// <summary>5-minute slot within the day (0–287)</summary>
    public int FiveMinuteBucket { get; set; }

    /// <summary>Sum of speeds in km/h for incremental averaging</summary>
    public double SumSpeedKmh { get; set; }

    public int SampleCount { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
