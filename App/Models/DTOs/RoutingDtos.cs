using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTrafficDB.Shared.Models.DTOs;

/// <summary>
/// A single traffic jam detected on a route — one or more consecutive edges
/// where the live constrained speed is significantly below the free-flow baseline.
/// </summary>
public class TrafficJam
{
    /// <summary>
    /// Geometry of the jam as a precision-6 encoded polyline
    /// (same encoding as Valhalla route shapes).
    /// </summary>
    public string EncodedPolyline { get; set; } = string.Empty;

    /// <summary>
    /// Total additional travel time caused by this jam compared to free-flow, in minutes.
    /// </summary>
    public double DelayMinutes { get; set; }

    /// <summary>
    /// Average live (constrained) speed across the jam in km/h.
    /// </summary>
    public double LiveSpeedKmh { get; set; }

    /// <summary>
    /// Average free-flow speed across the jam in km/h.
    /// </summary>
    public double FreeflowSpeedKmh { get; set; }

    /// <summary>
    /// Delay as a percentage of the free-flow travel time over this jam segment.
    /// E.g. 50 means the jam takes 50% longer than free-flow.
    /// </summary>
    public double DelayPercent { get; set; }

    /// <summary>Length of the jam segment in kilometres.</summary>
    public double LengthKm { get; set; }
}

/// <summary>
/// Traffic analysis overlay for one route (primary or alternate).
/// </summary>
public class RouteTrafficAnalysis
{
    /// <summary>0 = primary route, 1–N = alternate routes.</summary>
    public int RouteIndex { get; set; }

    /// <summary>
    /// Estimated travel time under free-flow conditions (no traffic), in minutes.
    /// Computed from per-edge length ÷ freeflow speed.
    /// </summary>
    public double FreeflowDurationMinutes { get; set; }

    /// <summary>
    /// Estimated travel time under current live traffic, in minutes.
    /// Edges without live data use free-flow speed (no penalty).
    /// </summary>
    public double LiveDurationMinutes { get; set; }

    /// <summary>
    /// Total delay vs free-flow in minutes (LiveDurationMinutes − FreeflowDurationMinutes).
    /// </summary>
    public double TotalDelayMinutes { get; set; }

    /// <summary>
    /// Delay as a percentage of the free-flow duration.
    /// E.g. 20 means the route currently takes 20% longer than free-flow.
    /// </summary>
    public double DelayPercent { get; set; }

    /// <summary>
    /// Fraction of the route length (0–1) covered by live traffic data.
    /// 1.0 means every km has recent observations; 0 means no live data at all.
    /// </summary>
    public double TrafficCoverageRatio { get; set; }

    /// <summary>
    /// Detected traffic jams on this route, ordered from start to end.
    /// </summary>
    public List<TrafficJam> Jams { get; set; } = [];
}

/// <summary>
/// Enriched Valhalla route response: the original Valhalla JSON plus a
/// per-route traffic analysis overlay.
///
/// Route selection: all Valhalla candidates (always ≥10) are analysed for
/// traffic, ranked by ascending live travel time, then trimmed to the number
/// of routes the caller originally requested.  The Valhalla JSON is rebuilt
/// to match — the traffic-best route becomes the primary <c>trip</c>.
/// </summary>
public class EnrichedRouteResponse
{
    /// <summary>
    /// Number of routes the caller originally requested
    /// (1 + the <c>alternates</c> value in the request, defaulting to 1).
    /// </summary>
    [JsonPropertyName("requestedRoutes")]
    public int RequestedRoutes { get; set; }

    /// <summary>
    /// Number of routes actually returned after traffic-ranking and trimming.
    /// May be less than <c>requestedRoutes</c> when Valhalla could not find
    /// enough distinct alternatives.
    /// </summary>
    [JsonPropertyName("returnedRoutes")]
    public int ReturnedRoutes { get; set; }

    /// <summary>
    /// Valhalla route response, rebuilt to contain only the
    /// traffic-ranked routes in order (best first).
    /// <c>trip</c> is the traffic-best route; <c>alternates</c> holds the rest.
    /// </summary>
    [JsonPropertyName("valhallaRoute")]
    public JsonElement ValhallaRoute { get; set; }

    /// <summary>
    /// Traffic analysis for each returned route, in the same order as the
    /// rebuilt Valhalla response (index 0 = traffic-best).
    /// </summary>
    [JsonPropertyName("trafficAnalysis")]
    public List<RouteTrafficAnalysis> TrafficAnalysis { get; set; } = [];
}
