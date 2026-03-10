using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>
/// Administrative endpoints. Requires an admin JWT (isAdmin claim = true).
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "AdminOnly")]
[Produces("application/json")]
public class AdminController(AppDbContext db, AbuseDetectionService abuseDetection, JwtService jwtService) : ControllerBase
{
    /// <summary>Get service-wide statistics.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(AdminStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var yesterday = DateTime.UtcNow.AddHours(-24);
        var now = DateTime.UtcNow;

        return Ok(new AdminStatsResponse
        {
            TotalObservations = await db.SpeedObservations.LongCountAsync(),
            ObservationsLast24h = await db.SpeedObservations.LongCountAsync(o => o.CreatedAt >= yesterday),
            ActiveApiKeys = await db.ApiKeys.CountAsync(k => k.IsActive),
            AggregatedEdges = await db.RoadSpeedAggregates.Select(a => a.ValhallaEdgeId).Distinct().CountAsync(),
            BlockedIps = await db.BlockedIps.CountAsync(b => b.ExpiresAt == null || b.ExpiresAt > now)
        });
    }

    /// <summary>List all blocked IP hashes (raw IPs are never stored).</summary>
    [HttpGet("blocked-ips")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBlockedIps()
    {
        var now = DateTime.UtcNow;
        var blocked = await db.BlockedIps
            .Where(b => b.ExpiresAt == null || b.ExpiresAt > now)
            .OrderByDescending(b => b.BlockedAt)
            .Select(b => new
            {
                b.Id,
                b.IpHash,
                b.Reason,
                b.IsManual,
                b.BlockedAt,
                b.ExpiresAt
            })
            .ToListAsync();

        return Ok(blocked);
    }

    /// <summary>Manually block an IP address.</summary>
    [HttpPost("block-ip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BlockIp([FromBody] BlockIpRequest request)
    {
        var ipHash = jwtService.HashIp(request.Ip);
        var now = DateTime.UtcNow;

        if (await db.BlockedIps.AnyAsync(b => b.IpHash == ipHash && (b.ExpiresAt == null || b.ExpiresAt > now)))
            return Conflict(new { error = "IP is already blocked." });

        await abuseDetection.ManualBlockAsync(request.Ip, request.Reason, request.ExpiresAt);
        return Ok(new { message = "IP blocked successfully." });
    }

    /// <summary>Unblock an IP address by its record ID.</summary>
    [HttpDelete("blocked-ips/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnblockIp(int id)
    {
        var entry = await db.BlockedIps.FindAsync(id);
        if (entry == null) return NotFound();

        await abuseDetection.UnblockAsync(id);
        return Ok(new { message = "IP unblocked." });
    }

    /// <summary>List all registered API keys (hashed — raw keys are never stored).</summary>
    [HttpGet("api-keys")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetApiKeys()
    {
        var keys = await db.ApiKeys
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new
            {
                k.Id,
                k.Name,
                k.IsActive,
                k.IsAdmin,
                k.CreatedAt,
                k.LastUsedAt,
                k.TotalContributions
            })
            .ToListAsync();

        return Ok(keys);
    }

    /// <summary>Deactivate an API key.</summary>
    [HttpDelete("api-keys/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateKey(int id)
    {
        var key = await db.ApiKeys.FindAsync(id);
        if (key == null) return NotFound();

        key.IsActive = false;
        await db.SaveChangesAsync();
        return Ok(new { message = $"API key for '{key.Name}' deactivated." });
    }
}
