using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenTrafficDB.AggregateWorker.Services;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models.DTOs;

namespace OpenTrafficDB.AggregateWorker.Controllers;

[ApiController]
[Route("internal/jobs")]
[Authorize]
public class JobController(
    ValhallaMapMatchingService matchingService,
    TrafficAggregationService aggregationService,
    AppDbContext db) : ControllerBase
{
    [HttpPost("match")]
    public async Task<IActionResult> Match([FromBody] MatchJobRequest req, CancellationToken ct)
    {
        var result = await matchingService.ProcessRangeAsync(req.TripIds, db, ct);
        return Ok(result);
    }

    [HttpPost("aggregate")]
    public async Task<IActionResult> Aggregate([FromBody] AggregateJobRequest req, CancellationToken ct)
    {
        var result = await aggregationService.ReaggregateRangeAsync(req.TripIds, ct);
        return Ok(result);
    }

    [HttpPost("finalize")]
    public async Task<IActionResult> Finalize(CancellationToken ct)
    {
        await aggregationService.FinalizeAsync(ct);
        return Ok();
    }
}
