namespace OpenTrafficDB.Models;

/// <summary>
/// Pre-aggregated speed data per OSM directed edge per 5-minute time bucket.
/// Key: (OsmWayId, IsForward, DayOfWeek, FiveMinuteBucket).
/// ValhallaEdgeId is cached here for Valhalla CSV export; it can become stale
/// when Valhalla's tile data is rebuilt but the OSM key remains stable.
///
/// BucketIndex = dayOfWeek * 288 + minuteOfDay / 5  (range 0–2015)
/// </summary>
public class EdgeSpeedAggregate
{
    public long Id { get; set; }

    /// <summary>Stable OSM way identifier</summary>
    public long OsmWayId { get; set; }

    /// <summary>True = heading 0–179° (forward); False = heading 180–359° (reverse)</summary>
    public bool IsForward { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (matches C# DayOfWeek)</summary>
    public int DayOfWeek { get; set; }

    /// <summary>5-minute slot within the day (0–287)</summary>
    public int FiveMinuteBucket { get; set; }

    /// <summary>Sum of speeds in km/h for incremental averaging</summary>
    public double SumSpeedKmh { get; set; }

    public int SampleCount { get; set; }

    /// <summary>
    /// Cached Valhalla GraphId for this edge — used when generating Valhalla
    /// predicted-traffic CSV. May be null or stale after a Valhalla map rebuild.
    /// </summary>
    public long? ValhallaEdgeId { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
