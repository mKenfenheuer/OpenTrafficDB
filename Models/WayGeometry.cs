namespace OpenTrafficDB.Models;

/// <summary>
/// Stores the shape of an OSM way as a Valhalla-encoded polyline (precision 6)
/// alongside the bounding box for fast spatial pre-filtering.
///
/// Populated asynchronously by WayGeometryBackgroundService via the Overpass API.
/// Used when generating vector traffic tiles.
/// </summary>
public class WayGeometry
{
    public long Id { get; set; }

    /// <summary>OSM way ID — matches OsmWayId on SpeedObservation / WaySpeedAggregate.</summary>
    public long OsmWayId { get; set; }

    // ── Bounding box (for quick spatial pre-filtering) ─────────────────────
    public double MinLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLat { get; set; }
    public double MaxLon { get; set; }

    /// <summary>
    /// Way geometry encoded as a Google/Valhalla polyline at precision 6.
    /// Decode with standard polyline decoding (divide by 1e6).
    /// </summary>
    public string EncodedPolyline { get; set; } = string.Empty;

    /// <summary>
    /// Parsed OSM maxspeed tag in km/h, if present.
    /// Handles numeric values ("50"), country defaults ("DE:urban" → 50, "DE:rural" → 100, etc.),
    /// and "none" / "unlimited" → null.
    /// </summary>
    public double? MaxSpeedKmh { get; set; }

    /// <summary>Number of nodes (lat/lon pairs) in the original OSM geometry.</summary>
    public int NodeCount { get; set; }

    public DateTime FetchedAt { get; set; }
}
