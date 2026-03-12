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

    /// <summary>Stable OSM way identifier. Populated after server-side map matching.</summary>
    public long? OsmWayId { get; set; }

    /// <summary>Max allowed speed in km/h for the matched edge. Populated after map matching.</summary>
    public double? SpeedLimitKmh { get; set; }

    /// <summary>
    /// H3 cell index at resolution 9 (~174 m hexagons) for spatial aggregation.
    /// Stored as signed long (raw ulong bits). Computed from Lat/Lon on map-match.
    /// </summary>
    public long? H3CellIndex { get; set; }

    /// <summary>
    /// True once this observation has been folded into WaySpeedAggregate and H3SpeedAggregate.
    /// Aggregated observations outside the live window (>1 h) are deleted by the background service.
    /// </summary>
    public bool IsAggregated { get; set; }

    /// <summary>SHA-256 + pepper of contributor IP — never store raw IP</summary>
    public string IpHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
