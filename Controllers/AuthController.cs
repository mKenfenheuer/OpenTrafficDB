using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>API key registration and JWT token issuance.</summary>
[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController(AppDbContext db, JwtService jwtService, AbuseDetectionService abuseDetection) : ControllerBase
{
    /// <summary>
    /// Register a new contributor account and receive a permanent API key.
    /// The raw API key is shown exactly once — store it securely.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!abuseDetection.CheckRegisterRate(ip))
            return StatusCode(429, new { error = "Too many registration attempts from this IP. Try again later." });

        var emailHash = jwtService.HashEmail(request.Email);
        if (await db.ApiKeys.AnyAsync(k => k.EmailHash == emailHash))
            return Conflict(new { error = "An account for this email already exists." });

        var rawKey = jwtService.GenerateRawApiKey();

        db.ApiKeys.Add(new ApiKey
        {
            KeyHash = jwtService.HashApiKey(rawKey),
            KeyPrefix = jwtService.GetKeyPrefix(rawKey),
            Name = request.Name.Trim(),
            EmailHash = emailHash,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(Register), new RegisterResponse
        {
            ApiKey = rawKey,
            Message = "Save this API key securely — it will not be shown again. " +
                      "Use POST /api/v1/auth/token to exchange it for a JWT access token."
        });
    }

    /// <summary>
    /// Exchange an API key for a short-lived JWT access token.
    /// Include the token in all requests: Authorization: Bearer {token}
    /// </summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetToken([FromBody] TokenRequest request)
    {
        var prefix = jwtService.GetKeyPrefix(request.ApiKey);
        var hash = jwtService.HashApiKey(request.ApiKey);

        var apiKey = await db.ApiKeys
            .FirstOrDefaultAsync(k => k.KeyPrefix == prefix && k.KeyHash == hash && k.IsActive);

        if (apiKey == null)
            return Unauthorized(new { error = "Invalid or deactivated API key." });

        apiKey.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var (token, expiresAt) = jwtService.GenerateJwt(apiKey);
        return Ok(new TokenResponse { AccessToken = token, ExpiresAt = expiresAt });
    }
}
