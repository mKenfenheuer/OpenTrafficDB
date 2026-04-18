namespace OpenTrafficDB.Shared.Models;

/// <summary>
/// Aggregated hazard clustered from raw user reports.
///
/// Keyed on (ClusterGridX, ClusterGridY, HeadingBucket, HazardType) — this lets a single
/// intersection host several distinct aggregates (e.g. four red-light cameras pointing in
/// four different directions) without conflating them by OSM edge alone.
///
/// Map-matching to an OSM edge is performed lazily by the background
/// HazardAggregationService via OpenTrafficRouting's /trace/localize/bulk endpoint;
/// <see cref="OsmWayId"/>, <see cref="StreetName"/>, <see cref="MatchedLatitude"/> and
/// <see cref="MatchedLongitude"/> are only populated once matching succeeds.
/// </summary>
public class HazardAggregate
{
    public long Id { get; set; }

    /// <summary>Latitude grid cell index (lat × ClusterGridDivisor, truncated).</summary>
    public int ClusterGridX { get; set; }
    /// <summary>Longitude grid cell index (lon × ClusterGridDivisor, truncated).</summary>
    public int ClusterGridY { get; set; }
    /// <summary>Heading bucket (0–7, one per 45° sector of the compass).</summary>
    public int HeadingBucket { get; set; }
    public HazardType HazardType { get; set; }

    /// <summary>Centroid latitude of all contributing reports.</summary>
    public double Latitude { get; set; }
    /// <summary>Centroid longitude of all contributing reports.</summary>
    public double Longitude { get; set; }

    /// <summary>Average travel direction (degrees, 0–360) across all reports of this hazard.</summary>
    public float Heading { get; set; }
    /// <summary>Running sum of sin(heading) — used to incrementally update the average direction.</summary>
    public double HeadingSinSum { get; set; }
    /// <summary>Running sum of cos(heading) — used to incrementally update the average direction.</summary>
    public double HeadingCosSum { get; set; }

    // ── Map-matched edge (populated by HazardAggregationService) ────────────────

    /// <summary>OSM way ID the cluster centroid was last matched to, null until matched.</summary>
    public long? OsmWayId { get; set; }
    /// <summary>Street name of the matched OSM edge, if any.</summary>
    public string? StreetName { get; set; }
    /// <summary>Snapped latitude returned by the map-matcher for the cluster centroid.</summary>
    public double? MatchedLatitude { get; set; }
    /// <summary>Snapped longitude returned by the map-matcher for the cluster centroid.</summary>
    public double? MatchedLongitude { get; set; }
    /// <summary>Centroid latitude at the time of the last successful map-match (for drift detection).</summary>
    public double? LastMatchedCentroidLat { get; set; }
    /// <summary>Centroid longitude at the time of the last successful map-match (for drift detection).</summary>
    public double? LastMatchedCentroidLon { get; set; }
    /// <summary>UTC time of the last successful map-match, null if never matched.</summary>
    public DateTime? LastMatchedAt { get; set; }

    /// <summary>Optional JSON metadata from the most recent report (e.g. {"max_speed":50}).</summary>
    public string? Metadata { get; set; }

    public int ConfirmationCount { get; set; }
    /// <summary>
    /// True when the hazard has been confirmed at least PermanentConfirmationThreshold
    /// times AND was first reported within the last 90 days.
    /// </summary>
    public bool IsPermanent { get; set; }

    public DateTime FirstReportedAt { get; set; }
    public DateTime LastConfirmedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
