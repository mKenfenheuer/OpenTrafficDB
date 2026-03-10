using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>Machine/app authentication: exchange an API key for an IP-bound JWT.</summary>
[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController(AppDbContext db, JwtService jwtService) : ControllerBase
{
    /// <summary>
    /// Exchange an API key for a short-lived JWT access token.
    /// The token is bound to your current IP address — it becomes invalid if your IP changes.
    /// Create API keys on your profile page at /profile.html
    /// Include the token in all API requests: Authorization: Bearer {token}
    /// </summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetToken([FromBody] TokenRequest request)
    {
        var prefix = jwtService.GetKeyPrefix(request.ApiKey);
        var hash   = jwtService.HashApiKey(request.ApiKey);

        var apiKey = await db.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k =>
                k.KeyPrefix == prefix &&
                k.KeyHash   == hash   &&
                k.IsActive            &&
                k.User.IsActive       &&
                k.User.IsEmailVerified);

        if (apiKey == null)
            return Unauthorized(new { error = "Invalid or deactivated API key." });

        var ip     = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ipHash = jwtService.HashIp(ip);

        apiKey.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var (token, expiresAt) = jwtService.GenerateJwt(apiKey.User, apiKey.Id, ipHash);
        return Ok(new TokenResponse
        {
            AccessToken = token,
            ExpiresAt   = expiresAt,
            Role        = apiKey.User.Role.ToString(),
            Name        = apiKey.User.Name
        });
    }
}
