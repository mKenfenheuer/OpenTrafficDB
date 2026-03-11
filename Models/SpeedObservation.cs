namespace OpenTrafficDB.Models;

public class SpeedObservation
{
    public long Id { get; set; }

    public int ApiKeyId { get; set; }
    public ApiKey ApiKey { get; set; } = null!;

    /// <summary>GPS timestamp (UTC)</summary>
    public DateTime Timestamp { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Speed in meters per second</summary>
    public double SpeedMs { get; set; }

    /// <summary>Heading in degrees 0-360, null if unavailable</summary>
    public float? Heading { get; set; }

    /// <summary>Altitude in meters, null if unavailable</summary>
    public float? AltitudeMeters { get; set; }

    /// <summary>GPS horizontal accuracy in meters (lower = better)</summary>
    public float GpsAccuracyMeters { get; set; }

    /// <summary>Anonymized trip identifier hashed trip id by the user</summary>
    public string TripId { get; set; } = string.Empty;

    /// <summary>
    /// Stable OSM way identifier. Populated after server-side map matching.
    /// Replaces the unstable Valhalla GraphId as the primary edge reference.
    /// </summary>
    public long? OsmWayId { get; set; }

    /// <summary>
    /// True = edge traversed in the heading-forward direction (bearing 0–179°);
    /// False = reverse direction (180–359°). Derived from Valhalla begin_heading
    /// to separate opposing traffic flows on the same OSM way.
    /// Null until map-matched.
    /// </summary>
    public bool? IsForward { get; set; }

    /// <summary>
    /// H3 cell index at resolution 9 (~174 m hexagons) for spatial aggregation.
    /// Computed from Latitude/Longitude on ingest; stored as signed long (raw ulong bits).
    /// </summary>
    public long? H3CellIndex { get; set; }

    /// <summary>SHA-256 + pepper of contributor IP — never store raw IP</summary>
    public string IpHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
