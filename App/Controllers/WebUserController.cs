using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.App.Data;
using OpenTrafficDB.App.Models;
using OpenTrafficDB.App.Models.DTOs;
using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Controllers;

/// <summary>User profile and API key management for authenticated web (cookie) sessions.</summary>
[ApiController]
[Route("web")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
[Produces("application/json")]
[ApiExplorerSettings(IgnoreApi = true)]
public class WebUserController(
    AppDbContext db,
    ApiKeyService keyService,
    PasswordService passwordService,
    CryptoService crypto,
    IEmailService emailService,
    IConfiguration config) : ControllerBase
{
    private string BaseUrl => config["BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";

    private int ContributorThreshold =>
        int.TryParse(config["Security:ContributorThreshold"], out var t) ? t : 100;

    private int CurrentUserId =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    private string CurrentEmail =>
        User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

    // ── Profile ───────────────────────────────────────────────────────────────

    /// <summary>Get the current user's profile.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(WebProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile()
    {
        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound();

        var toNext = user.Role == UserRole.User
            ? Math.Max(0, ContributorThreshold - user.TotalContributions)
            : 0;

        return Ok(new WebProfileResponse
        {
            Id                      = user.Id,
            Name                    = user.Name,
            Email                   = CurrentEmail,
            Role                    = user.Role.ToString(),
            TotalContributions      = user.TotalContributions,
            ContributionsToNextRank = toNext,
            CreatedAt               = user.CreatedAt
        });
    }

    /// <summary>Update the current user's display name.</summary>
    [HttpPut("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateName([FromBody] UpdateNameRequest request)
    {
        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound();

        user.Name = request.Name.Trim();
        await db.SaveChangesAsync();

        // Refresh the name claim in the cookie so the UI reflects the change immediately
        var claims = User.Claims
            .Where(c => c.Type != ClaimTypes.Name)
            .Append(new System.Security.Claims.Claim(ClaimTypes.Name, user.Name))
            .ToList();

        var identity  = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        return Ok(new { message = "Display name updated.", name = user.Name });
    }

    /// <summary>Change the current user's password. Requires the current password for verification.</summary>
    [HttpPut("me/password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (request.NewPassword != request.NewPasswordConfirm)
            return BadRequest(new { error = "New passwords do not match." });

        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound();

        if (!passwordService.VerifyPassword(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { error = "Current password is incorrect." });

        user.PasswordHash = passwordService.HashPassword(request.NewPassword);
        await db.SaveChangesAsync();

        return Ok(new { message = "Password updated successfully." });
    }

    /// <summary>
    /// Initiate an email address change. Requires the current password for verification.
    /// A confirmation link is sent to the new email address; the change takes effect only after clicking it.
    /// </summary>
    [HttpPost("me/email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
    {
        var user = await db.Users.FindAsync(CurrentUserId);
        if (user == null) return NotFound();

        if (!passwordService.VerifyPassword(request.Password, user.PasswordHash))
            return BadRequest(new { error = "Password is incorrect." });

        var newEmailHash = crypto.HashEmail(request.NewEmail);

        if (newEmailHash == user.EmailHash)
            return BadRequest(new { error = "The new email address is the same as the current one." });

        if (await db.Users.AnyAsync(u => u.EmailHash == newEmailHash))
            return Conflict(new { error = "An account with that email address already exists." });

        var token  = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                         .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        user.PendingEmailHash  = newEmailHash;
        user.VerificationToken  = token;
        user.VerificationExpiry = DateTime.UtcNow.AddHours(24);
        await db.SaveChangesAsync();

        var verifyUrl = $"{BaseUrl}/auth/verify?token={Uri.EscapeDataString(token)}";
        await emailService.SendEmailChangeVerificationAsync(request.NewEmail, verifyUrl);

        return Ok(new { message = "A confirmation link has been sent to your new email address. Click it to complete the change." });
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    /// <summary>Global platform statistics shown on the dashboard.</summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(WebStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var yesterday = DateTime.UtcNow.AddHours(-24);

        return Ok(new WebStatsResponse
        {
            TotalObservations   = await db.SpeedObservations.LongCountAsync(),
            ObservationsLast24h = await db.SpeedObservations.LongCountAsync(o => o.CreatedAt >= yesterday),
            TotalUsers          = await db.Users.CountAsync(u => u.IsActive),
            ContributorCount    = await db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Contributor),
            AggregatedWays     = await db.WaySpeedAggregates.Select(a => a.OsmWayId).Distinct().CountAsync()
        });
    }

    // ── API Keys ──────────────────────────────────────────────────────────────

    /// <summary>List all API keys for the current user.</summary>
    [HttpGet("me/keys")]
    [ProducesResponseType(typeof(IEnumerable<ApiKeyResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetKeys()
    {
        var keys = await keyService.GetKeysAsync(CurrentUserId);
        return Ok(keys);
    }

    /// <summary>
    /// Create a new API key.
    /// The raw key is shown exactly once — store it securely.
    /// Use POST /api/v1/auth/token with this key to get a JWT for API calls.
    /// </summary>
    [HttpPost("me/keys")]
    [ProducesResponseType(typeof(CreateKeyResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateKey([FromBody] CreateKeyRequest request)
    {
        var (rawKey, key) = await keyService.CreateKeyAsync(CurrentUserId, request.Label);

        return CreatedAtAction(nameof(GetKeys), new CreateKeyResponse
        {
            ApiKey  = rawKey,
            KeyId   = key.Id,
            Label   = key.Label,
            Message = "Store this API key securely — it will not be shown again. " +
                      "Use it directly as the Bearer token: Authorization: Bearer <key>"
        });
    }

    /// <summary>Revoke one of your API keys.</summary>
    [HttpDelete("me/keys/{keyId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeKey(int keyId)
    {
        var (ok, error) = await keyService.RevokeKeyAsync(CurrentUserId, keyId);
        if (error != null) return BadRequest(new { error });

        // Check if key actually existed (RevokeKeyAsync returns ok=true only if found)
        if (!ok) return NotFound();

        return Ok(new { message = "Key revoked." });
    }

    /// <summary>Update the label of one of your API keys.</summary>
    [HttpPut("me/keys/{keyId:int}/label")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateKeyLabel(int keyId, [FromBody] UpdateKeyLabelRequest request)
    {
        var updated = await keyService.UpdateLabelAsync(CurrentUserId, keyId, request.Label);
        if (!updated) return NotFound();

        return Ok(new { message = "Label updated.", label = request.Label.Trim() });
    }
}
