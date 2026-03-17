using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.AggregateWorker.Controllers;

[ApiController]
[Route("internal/traffic")]
[Authorize]
public class TrafficInternalController(TrafficAggregationService aggregation) : ControllerBase
{
    [HttpGet("live")]
    public async Task<IActionResult> GetLiveSpeeds([FromQuery] LiveTrafficQuery query, CancellationToken ct)
    {
        if (query.MinLat >= query.MaxLat || query.MinLon >= query.MaxLon)
            return BadRequest(new { error = "Invalid bounding box." });

        if (Math.Abs(query.MaxLat - query.MinLat) > 1.0 || Math.Abs(query.MaxLon - query.MinLon) > 1.0)
            return BadRequest(new { error = "Bounding box too large. Maximum size is 1° × 1°." });

        return Ok(await aggregation.GetLiveSpeedsAsync(query, ct));
    }

    [HttpGet("current/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetCurrentWayCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"current_way_traffic.csv\"";
        await aggregation.WriteCurrentWayCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("live/way/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetLiveWayCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_way_traffic.csv\"";
        await aggregation.WriteLiveWayCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("historic/way/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetHistoricWayCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"historic_way_traffic.csv\"";
        await aggregation.WriteHistoricWayCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("historic/h3/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetHistoricH3Csv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"historic_h3_traffic.csv\"";
        await aggregation.WriteHistoricH3CsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("valhalla/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetValhallaExportCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"valhalla_traffic.csv\"";
        await aggregation.WriteValhallaExportCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("live/h3/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetLiveH3Csv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_h3_traffic.csv\"";
        await aggregation.WriteLiveH3CsvAsync(Response.Body, ct);
        return new EmptyResult();
    }
}
