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
    /// <summary>
    /// Average travel direction (degrees, 0–360) across all reports of this hazard.
    /// Used by navigation apps to determine if the hazard applies to their direction
    /// on multi-direction edges. Null if no report supplied a heading.
    /// </summary>
    public float? Heading { get; set; }
    /// <summary>Running sum of sin(heading) — used to incrementally update the average direction.</summary>
    public double HeadingSinSum { get; set; }
    /// <summary>Running sum of cos(heading) — used to incrementally update the average direction.</summary>
    public double HeadingCosSum { get; set; }
    /// <summary>Number of reports that contributed a heading value.</summary>
    public int HeadingSampleCount { get; set; }
    /// <summary>Street name (OSM "name" tag) of the matched edge, if known.</summary>
    public string? StreetName { get; set; }
    /// <summary>Optional JSON metadata from the most recent report (e.g. {"max_speed":50}).</summary>
    public string? Metadata { get; set; }
    public int ConfirmationCount { get; set; }
    /// <summary>
    /// True when the hazard has been confirmed multiple times over the last 90 days,
    /// indicating a persistent/permanent hazard (e.g. a fixed speed camera) rather
    /// than a transient one.
    /// </summary>
    public bool IsPermanent { get; set; }
    public DateTime FirstReportedAt { get; set; }
    public DateTime LastConfirmedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
