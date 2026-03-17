using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTrafficDB.App.Models.DTOs;

public class TrafficJam
{
    public string EncodedPolyline { get; set; } = string.Empty;
    public double DelayMinutes { get; set; }
    public double LiveSpeedKmh { get; set; }
    public double FreeflowSpeedKmh { get; set; }
    public double DelayPercent { get; set; }
    public double LengthKm { get; set; }
}

public class RouteTrafficAnalysis
{
    public int RouteIndex { get; set; }
    public double FreeflowDurationMinutes { get; set; }
    public double LiveDurationMinutes { get; set; }
    public double TotalDelayMinutes { get; set; }
    public double DelayPercent { get; set; }
    public double TrafficCoverageRatio { get; set; }
    public List<TrafficJam> Jams { get; set; } = [];
}

public class EnrichedRouteResponse
{
    [JsonPropertyName("requestedRoutes")]
    public int RequestedRoutes { get; set; }

    [JsonPropertyName("returnedRoutes")]
    public int ReturnedRoutes { get; set; }

    [JsonPropertyName("valhallaRoute")]
    public JsonElement ValhallaRoute { get; set; }

    [JsonPropertyName("trafficAnalysis")]
    public List<RouteTrafficAnalysis> TrafficAnalysis { get; set; } = [];
}
