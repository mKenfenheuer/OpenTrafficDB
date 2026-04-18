namespace OpenTrafficDB.Shared.Models;

/// <summary>
/// Aggregated hazard on a map-matched OSM edge. Multiple user reports of the
/// same hazard type on the same edge are merged into a single aggregate.
/// </summary>
public class HazardAggregate
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public HazardType HazardType { get; set; }
    /// <summary>Representative latitude (from the closest point on the matched edge).</summary>
    public double Latitude { get; set; }
    /// <summary>Representative longitude (from the closest point on the matched edge).</summary>
    public double Longitude { get; set; }
    /// <summary>Optional JSON metadata from the most recent report (e.g. {"max_speed":50}).</summary>
    public string? Metadata { get; set; }
    public int ConfirmationCount { get; set; }
    public DateTime FirstReportedAt { get; set; }
    public DateTime LastConfirmedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
