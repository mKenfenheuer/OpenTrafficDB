namespace OpenTrafficDB.App.Models;

public class SpeedObservation
{
    public long Id { get; set; }
    public int ApiKeyId { get; set; }
    public ApiKey ApiKey { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double SpeedMs { get; set; }
    public float? Heading { get; set; }
    public float? AltitudeMeters { get; set; }
    public float GpsAccuracyMeters { get; set; }
    public string TripId { get; set; } = string.Empty;
    public long? OsmWayId { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public long? H3CellIndex { get; set; }
    public bool IsAggregated { get; set; }
    public bool RoadWorks { get; set; }
    public string IpHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
