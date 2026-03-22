using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTrafficDB.Shared.Models.DTOs;

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

    /// <summary>Total route distance in kilometres.</summary>
    public double DistanceKm { get; set; }

    /// <summary>
    /// Distance travelled per minute of live travel time (km/min).
    /// Lower values indicate a shorter route for roughly the same travel time.
    /// </summary>
    public double DistancePerMinuteKm { get; set; }

    /// <summary>Average live speed across the route in km/h.</summary>
    public double AverageSpeedKmh { get; set; }

    /// <summary>
    /// Estimated CO2 emissions in grams for the route, assuming a standard
    /// European passenger car (~1.4 L petrol). Uses a speed-dependent emission
    /// model: higher emissions at very low (stop-and-go) and very high speeds.
    /// </summary>
    public double Co2GramsEstimate { get; set; }

    /// <summary>
    /// Composite eco score used for route ranking (lower is better).
    /// Combines CO2 estimate with a distance-per-minute penalty to favour
    /// shorter routes when travel times are comparable.
    /// </summary>
    public double EcoScore { get; set; }

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
