namespace OpenTrafficDB.Models;

/// <summary>
/// Pre-aggregated speed data per OSM way per 5-minute time bucket.
/// Key: (OsmWayId, DayOfWeek, FiveMinuteBucket)
/// </summary>
public class WaySpeedAggregate
{
    public long Id { get; set; }

    /// <summary>Stable OSM way identifier</summary>
    public long OsmWayId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (matches C# DayOfWeek)</summary>
    public int DayOfWeek { get; set; }

    /// <summary>5-minute slot within the day (0–287)</summary>
    public int FiveMinuteBucket { get; set; }

    /// <summary>Sum of speeds in km/h for incremental averaging</summary>
    public double SumSpeedKmh { get; set; }

    public int SampleCount { get; set; }

    /// <summary>Max allowed speed in km/h from Valhalla edge data. Updated on each aggregation pass.</summary>
    public double? SpeedLimitKmh { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the first observation for this (way, dow, bucket) was received.
    /// Used to compute how many weekly slots have elapsed, so that weeks without
    /// observations can be treated as freeflow when averaging.
    /// </summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
}
