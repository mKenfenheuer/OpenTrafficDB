using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>User profile and API key self-management.</summary>
[ApiController]
[Route("api/v1/user")]
[Authorize]
[Produces("application/json")]
public class UserController(AppDbContext db, JwtService jwtService, IConfiguration config) : ControllerBase
{
    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    private int CurrentUserId => int.Parse(User.FindFirstValue("sub") ?? "0");
    private int CurrentKeyId  => int.Parse(User.FindFirstValue("keyId") ?? "0");

    // ── Profile ───────────────────────────────────────────────────────────────

    /// <summary>Get the profile of the currently authenticated user.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile()
    {
        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound();

        var threshold = ContributorThreshold;
        var toNext = user.Role == UserRole.User
            ? Math.Max(0, threshold - user.TotalContributions)
            : 0;

        return Ok(new UserProfileResponse
        {
            Id = user.Id,
            Name = user.Name,
            Role = user.Role.ToString(),
            TotalContributions = user.TotalContributions,
            CreatedAt = user.CreatedAt,
            ContributionsToNextRank = toNext
        });
    }

    // ── API Keys ──────────────────────────────────────────────────────────────

    /// <summary>List all API keys belonging to the current user.</summary>
    [HttpGet("keys")]
    [ProducesResponseType(typeof(IEnumerable<ApiKeyResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetKeys()
    {
        var currentKeyId = CurrentKeyId;
        var keys = await db.ApiKeys
            .Where(k => k.UserId == CurrentUserId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyResponse
            {
                Id = k.Id,
                Label = k.Label,
                IsActive = k.IsActive,
                CreatedAt = k.CreatedAt,
                LastUsedAt = k.LastUsedAt,
                IsCurrent = k.Id == currentKeyId
            })
            .ToListAsync();

        return Ok(keys);
    }

    /// <summary>
    /// Create a new API key for this account.
    /// Use this to issue separate keys per device or integration.
    /// The raw key is shown exactly once — store it securely.
    /// </summary>
    [HttpPost("keys")]
    [ProducesResponseType(typeof(CreateKeyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateKey([FromBody] CreateKeyRequest request)
    {
        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound();

        var rawKey = jwtService.GenerateRawApiKey();
        var label = string.IsNullOrWhiteSpace(request.Label) ? "Default" : request.Label.Trim();

        var apiKey = new ApiKey
        {
            UserId = user.Id,
            KeyHash = jwtService.HashApiKey(rawKey),
            KeyPrefix = jwtService.GetKeyPrefix(rawKey),
            Label = label,
            CreatedAt = DateTime.UtcNow
        };

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetKeys), new CreateKeyResponse
        {
            ApiKey = rawKey,
            KeyId = apiKey.Id,
            Label = label,
            Message = "Store this key securely — it will not be shown again. " +
                      "Use POST /api/v1/auth/token to get a JWT with this key."
        });
    }

    /// <summary>
    /// Revoke one of your API keys. You cannot revoke the key currently in use.
    /// </summary>
    [HttpDelete("keys/{keyId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeKey(int keyId)
    {
        if (keyId == CurrentKeyId)
            return BadRequest(new { error = "You cannot revoke the key you're currently using. Use a different key or create a new one first." });

        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == CurrentUserId);
        if (key == null) return NotFound();

        key.IsActive = false;
        await db.SaveChangesAsync();
        return Ok(new { message = $"Key '{key.Label}' revoked." });
    }

    /// <summary>Update the label of one of your API keys.</summary>
    [HttpPut("keys/{keyId:int}/label")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateKeyLabel(int keyId, [FromBody] UpdateKeyLabelRequest request)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == CurrentUserId);
        if (key == null) return NotFound();

        key.Label = request.Label.Trim();
        await db.SaveChangesAsync();
        return Ok(new { message = "Label updated.", label = key.Label });
    }
}
