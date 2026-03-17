namespace OpenTrafficDB.App.Models;

public class WayGeometry
{
    public long Id { get; set; }
    public long OsmWayId { get; set; }
    public double MinLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLat { get; set; }
    public double MaxLon { get; set; }
    public string EncodedPolyline { get; set; } = string.Empty;
    public double? MaxSpeedKmh { get; set; }
    public int NodeCount { get; set; }
    public DateTime FetchedAt { get; set; }
}
