using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.AggregateWorker.Services;

namespace OpenTrafficDB.AggregateWorker.Controllers;

[ApiController]
[Route("internal/traffic")]
[Authorize]
public class TrafficInternalController(TrafficAggregationService aggregation) : ControllerBase
{
    [HttpGet("valhalla/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetValhallaExportCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"valhalla_traffic.csv\"";
        await aggregation.WriteValhallaExportCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

    [HttpGet("live/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetLiveTrafficCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"live_traffic.csv\"";
        await aggregation.WriteLiveTrafficCsvAsync(Response.Body, ct);
        return new EmptyResult();
    }

}
