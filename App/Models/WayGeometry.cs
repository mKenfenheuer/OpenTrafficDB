using NetTopologySuite.Geometries;

namespace OpenTrafficDBModels;

public class WayGeometry
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public double MinLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLat { get; set; }
    public double MaxLon { get; set; }
    /// <summary>Way geometry as a PostGIS LineString (SRID 4326, coordinates: lon/lat).</summary>
    public Geometry? Geometry { get; set; }
    public double? MaxSpeedKmh { get; set; }
    /// <summary>Street name from the OSM "name" tag, if present.</summary>
    public string? Name { get; set; }
    public int NodeCount { get; set; }
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// Bounding box as a PostGIS geometry polygon (SRID 4326).
    /// Populated on insert/update, indexed with GIST for fast tile bbox queries.
    /// </summary>
    public Geometry? BBox { get; set; }
}
