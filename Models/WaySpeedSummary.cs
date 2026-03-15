namespace OpenTrafficDB.Models;

/// <summary>
/// Precomputed per-way speed summary.  Rebuilt by TrafficAggregationService whenever
/// new WaySpeedAggregate rows are written.
///
/// Freeflow  = 85th-percentile of per-bucket average speeds (uncongested baseline).
/// Constrained = rolling 30-minute live average (null when no recent observations).
/// P50 / P95 = median / 95th-percentile of the same bucket averages (for tile metadata).
/// </summary>
public class WaySpeedSummary
{
    public long Id { get; set; }

    /// <summary>OSM way ID — unique, matches OsmWayId on WaySpeedAggregate / WayGeometry.</summary>
    public long OsmWayId { get; set; }

    /// <summary>
    /// Uncongested (free-flow) speed in km/h — 85th percentile across all 5-minute
    /// time-bucket averages observed for this way.
    /// </summary>
    public double FreeflowSpeed { get; set; }

    /// <summary>
    /// Current constrained speed (live 30-min rolling average) in km/h.
    /// Null when no observations exist in the last 30 minutes.
    /// </summary>
    public double? ConstrainedSpeed { get; set; }

    /// <summary>Median (50th percentile) of per-bucket averages.</summary>
    public double P50Speed { get; set; }

    /// <summary>95th-percentile of per-bucket averages.</summary>
    public double P95Speed { get; set; }

    /// <summary>Speed limit from WayGeometry.MaxSpeedKmh or the latest observation.</summary>
    public double? SpeedLimitKmh { get; set; }

    /// <summary>Total sample count across all historic buckets.</summary>
    public int TotalSamples { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
