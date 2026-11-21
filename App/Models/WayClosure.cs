namespace OpenTrafficDBModels;

/// <summary>
/// A road that is currently closed. Closures arrive as polylines (e.g. closed TomTom flow
/// segments) and are map-matched as a whole trace, so a closure on a slip road or connector is
/// not mistaken for the parallel main carriageway, and every road between the line's vertices is
/// covered. Rows expire unless the source reports the closure again.
/// </summary>
public class WayClosure
{
    public long OsmWayId { get; set; }
    public DateTime FirstReportedAt { get; set; }
    public DateTime LastReportedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>Reporting source, e.g. "tomtom-flow".</summary>
    public string Source { get; set; } = "";
    /// <summary>OSM highway type of the matched road (e.g. "motorway", "motorway_link").</summary>
    public string? RoadType { get; set; }
}
