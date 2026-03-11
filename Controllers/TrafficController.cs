using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>
/// Endpoints for consuming aggregated traffic data, compatible with Valhalla routing.
/// </summary>
[ApiController]
[Route("api/v1/traffic")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public class TrafficController(
    TrafficAggregationService aggregation,
    AbuseDetectionService abuseDetection) : ControllerBase
{
    /// <summary>
    /// Export predicted traffic speeds in Valhalla CSV format (single flat file).
    /// Only edges with a cached Valhalla GraphId are included.
    /// Format: edge_id,freeflow_speed,constrained_speed
    ///   edge_id = "level/tile_id/id"
    ///   freeflow_speed   = typical night-time speed (km/h)
    ///   constrained_speed = typical day-time speed (km/h)
    /// </summary>
    [HttpGet("predicted/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPredictedCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded for data export." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"predicted_traffic.csv\"";

        await aggregation.WritePredictedCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Download a traffic.tar archive organised in Valhalla's tile folder hierarchy.
    /// Extract it to a folder and pass that folder to valhalla_add_predicted_traffic:
    ///   tar -xf traffic.tar -C /path/to/traffic_tiles/
    ///   valhalla_add_predicted_traffic --config valhalla.json -t /path/to/traffic_tiles/
    ///
    /// Archive structure: {level}/{tile_dir:D3}/{tile_file:D3}.csv
    /// Each CSV row: edge_id,freeflow_speed,constrained_speed
    /// </summary>
    [HttpGet("predicted/tar")]
    [Produces("application/x-tar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPredictedTar(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded for data export." });

        Response.ContentType = "application/x-tar";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"traffic.tar\"";

        await aggregation.WriteTrafficTarAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Get recent speed observations within a geographic bounding box for live traffic display.
    /// Returns GPS-level speed points. Maximum bounding box: 1° × 1°. Maximum age: 60 minutes.
    /// </summary>
    [HttpGet("live")]
    [ProducesResponseType(typeof(LiveTrafficResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveSpeeds([FromQuery] LiveTrafficQuery query, CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        if (query.MinLat >= query.MaxLat || query.MinLon >= query.MaxLon)
            return BadRequest(new { error = "Invalid bounding box: min values must be less than max values." });

        if (Math.Abs(query.MaxLat - query.MinLat) > 1.0 || Math.Abs(query.MaxLon - query.MinLon) > 1.0)
            return BadRequest(new { error = "Bounding box too large. Maximum size is 1° × 1°." });

        var result = await aggregation.GetLiveSpeedsAsync(query, ct);
        return Ok(result);
    }

    /// <summary>
    /// Live edge-level traffic statistics as CSV (rolling 30-minute window).
    /// Aggregates observations by OSM directed edge (way_id + direction).
    /// Format: osm_way_id,is_forward,avg_speed_kmh,sample_count
    ///   is_forward: 1 = heading 0–179°, 0 = heading 180–359°
    /// Useful for routing engines and map overlays that operate on road segments.
    /// </summary>
    [HttpGet("live/edge/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveEdgeCsv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_edge_traffic.csv\"";

        await aggregation.WriteLiveEdgeCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Live H3-grid traffic statistics as CSV (rolling 30-minute window, resolution 9 ~174 m).
    /// Aggregates observations by H3 hexagonal cell for spatial analytics.
    /// Format: h3_index,avg_speed_kmh,sample_count
    ///   h3_index: H3 cell id as lowercase hex string (e.g. 89283082803ffff)
    /// Useful for heatmaps and grid-based congestion analysis.
    /// </summary>
    [HttpGet("live/h3/csv")]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetLiveH3Csv(CancellationToken ct)
    {
        var ip    = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyId = User.FindFirstValue("keyId") ?? string.Empty;

        if (!abuseDetection.CheckReadRate(ip, keyId))
            return StatusCode(429, new { error = "Rate limit exceeded." });

        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_h3_traffic.csv\"";

        await aggregation.WriteLiveH3CsvAsync(Response.Body, ct);
        return new EmptyResult();
    }
}
