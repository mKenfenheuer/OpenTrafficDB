namespace OpenTrafficDB.Shared.Models;

/// <summary>
/// One row per aggregation job run, used to compute processing rates on the admin dashboard.
/// Old rows are purged periodically by FinalizeAsync.
/// </summary>
public class AggregationMetric
{
    public long Id              { get; set; }
    public DateTime ProcessedAt { get; set; }   // UTC
    public int ObservationCount { get; set; }
    public int WayCount         { get; set; }
}
