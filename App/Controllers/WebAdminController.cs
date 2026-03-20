using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>Administrative endpoints for web (cookie) sessions. Requires Admin role.</summary>
[ApiController]
[Route("web/admin")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "WebAdminOnly")]
[Produces("application/json")]
[ApiExplorerSettings(IgnoreApi = true)]
public class WebAdminController(AppDbContext db, AbuseDetectionService abuseDetection, CryptoService crypto, WorkerHealthService workerHealth, StatsHistoryService statsHistory) : ControllerBase
{
    private int CurrentUserId =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    // ── Stats ─────────────────────────────────────────────────────────────────

    /// <summary>Queue depths and ingest/processing rates for the aggregation pipeline.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var cutoff5 = DateTime.UtcNow.AddMinutes(-5);

        // matchingQueue: unmatched AND not yet given up on (actionable by coordinator)
        // aggregateQueue: matched but not yet aggregated
        // totalQueue: union of the two
        var matchingQueue   = await db.SpeedObservations.CountAsync(o => o.OsmWayId == null && !o.IsAggregated, ct);
        var aggregateQueue  = await db.SpeedObservations.CountAsync(o => o.OsmWayId != null && !o.IsAggregated, ct);
        var totalQueue      = matchingQueue + aggregateQueue;
        var ingestLast5Min  = await db.SpeedObservations.CountAsync(o => o.CreatedAt >= cutoff5, ct);
        var processedLast5Min = await db.AggregationMetrics
            .Where(m => m.ProcessedAt >= cutoff5)
            .SumAsync(m => (long)m.ObservationCount, ct);

        return Ok(new
        {
            totalQueue,
            matchingQueue,
            aggregateQueue,
            ingestPer5Min     = ingestLast5Min,
            ingestPerMin      = ingestLast5Min / 5,
            processedPer5Min  = processedLast5Min,
            processedPerMin   = processedLast5Min / 5,
        });
    }

    /// <summary>Returns the last hour of stats snapshots for chart pre-population.</summary>
    [HttpGet("stats/history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatsHistory()
    {
        var samples = statsHistory.GetHistory().Select(s => new
        {
            timestamp       = s.Timestamp,
            totalQueue      = s.TotalQueue,
            matchingQueue   = s.MatchingQueue,
            aggregateQueue  = s.AggregateQueue,
            ingestPerMin    = s.IngestPerMin,
            processedPerMin = s.ProcessedPerMin,
        });
        return Ok(samples);
    }

    // ── Workers ───────────────────────────────────────────────────────────────

    /// <summary>Get health and metrics for all worker services.</summary>
    [HttpGet("workers")]
    [ProducesResponseType(typeof(WorkerServiceInfo[]), StatusCodes.Status200OK)]
    public IActionResult GetWorkers() => Ok(workerHealth.GetAllStatuses());

    // ── Users ─────────────────────────────────────────────────────────────────

    /// <summary>List all user accounts.</summary>
    [HttpGet("users")]
    [ProducesResponseType(typeof(IEnumerable<AdminUserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers()
    {
        var users = await db.Users
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new AdminUserResponse
            {
                Id                 = u.Id,
                Name               = u.Name,
                Role               = u.Role.ToString(),
                IsActive           = u.IsActive,
                TotalContributions = u.TotalContributions,
                CreatedAt          = u.CreatedAt,
                KeyCount           = u.ApiKeys.Count(k => k.IsActive)
            })
            .ToListAsync();

        return Ok(users);
    }

    /// <summary>List all API keys for a specific user.</summary>
    [HttpGet("users/{userId:int}/keys")]
    [ProducesResponseType(typeof(IEnumerable<AdminKeyResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserKeys(int userId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId)) return NotFound();

        var keys = await db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new AdminKeyResponse
            {
                Id         = k.Id,
                Label      = k.Label,
                IsActive   = k.IsActive,
                CreatedAt  = k.CreatedAt,
                LastUsedAt = k.LastUsedAt
            })
            .ToListAsync();

        return Ok(keys);
    }

    /// <summary>Change the role of a user. Admins cannot demote themselves.</summary>
    [HttpPut("users/{userId:int}/role")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetRole(int userId, [FromBody] SetRoleRequest request)
    {
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var newRole))
            return BadRequest(new { error = $"Invalid role '{request.Role}'. Valid values: User, Contributor, Admin." });

        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        if (user.Id == CurrentUserId && user.Role == UserRole.Admin && newRole != UserRole.Admin)
            return BadRequest(new { error = "Admins cannot demote themselves." });

        user.Role = newRole;
        await db.SaveChangesAsync();
        return Ok(new { message = $"Role for '{user.Name}' set to {newRole}." });
    }

    /// <summary>Deactivate a user account.</summary>
    [HttpDelete("users/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateUser(int userId)
    {
        if (userId == CurrentUserId)
            return BadRequest(new { error = "You cannot deactivate your own account." });

        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        user.IsActive = false;
        await db.SaveChangesAsync();
        return Ok(new { message = $"Account for '{user.Name}' deactivated." });
    }

    /// <summary>Reactivate a previously deactivated user account.</summary>
    [HttpPost("users/{userId:int}/activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateUser(int userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        user.IsActive = true;
        await db.SaveChangesAsync();
        return Ok(new { message = $"Account for '{user.Name}' reactivated." });
    }

    // ── API Keys ──────────────────────────────────────────────────────────────

    /// <summary>Deactivate a specific API key.</summary>
    [HttpDelete("keys/{keyId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateKey(int keyId)
    {
        var key = await db.ApiKeys.Include(k => k.User).FirstOrDefaultAsync(k => k.Id == keyId);
        if (key == null) return NotFound();

        key.IsActive = false;
        await db.SaveChangesAsync();
        return Ok(new { message = $"Key '{key.Label}' for '{key.User.Name}' deactivated." });
    }

    // ── Blocked IPs ───────────────────────────────────────────────────────────

    /// <summary>List all blocked IP hashes.</summary>
    [HttpGet("blocked-ips")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBlockedIps()
    {
        var now = DateTime.UtcNow;
        var blocked = await db.BlockedIps
            .Where(b => b.ExpiresAt == null || b.ExpiresAt > now)
            .OrderByDescending(b => b.BlockedAt)
            .Select(b => new { b.Id, b.IpHash, b.Reason, b.IsManual, b.BlockedAt, b.ExpiresAt })
            .ToListAsync();

        return Ok(blocked);
    }

    /// <summary>Manually block an IP address.</summary>
    [HttpPost("block-ip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BlockIp([FromBody] BlockIpRequest request)
    {
        var ipHash = crypto.HashIp(request.Ip);
        var now = DateTime.UtcNow;

        if (await db.BlockedIps.AnyAsync(b => b.IpHash == ipHash && (b.ExpiresAt == null || b.ExpiresAt > now)))
            return Conflict(new { error = "IP is already blocked." });

        await abuseDetection.ManualBlockAsync(request.Ip, request.Reason, request.ExpiresAt);
        return Ok(new { message = "IP blocked successfully." });
    }

    /// <summary>Unblock an IP address by record ID.</summary>
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

    // ── Danger Zone ───────────────────────────────────────────────────────────

    /// <summary>
    /// Purge all gathered traffic data: speed observations, way aggregates, and way speed summaries.
    /// Geometries, users, API keys, and blocked IPs are preserved.
    /// </summary>
    [HttpDelete("data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> PurgeTrafficData()
    {
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"WaySpeedSummaries\", \"WaySpeedAggregates\", \"SpeedObservations\" RESTART IDENTITY");

        // Reset contribution counters — the observations are gone
        await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.TotalContributions, 0));

        return Ok(new { message = "All traffic data purged. Geometries, users, and API keys are intact." });
    }
}
