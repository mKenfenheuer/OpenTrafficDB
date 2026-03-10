using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;
using OpenTrafficDB.Models.DTOs;
using OpenTrafficDB.Services;

namespace OpenTrafficDB.Controllers;

/// <summary>Cookie-based authentication for human users (web UI).</summary>
[ApiController]
[Route("auth")]
[Produces("application/json")]
[ApiExplorerSettings(IgnoreApi = true)]
public class WebAuthController(
    AppDbContext db,
    CryptoService crypto,
    PasswordService passwordService,
    IEmailService emailService,
    AbuseDetectionService abuseDetection,
    IConfiguration config) : ControllerBase
{
    private string BaseUrl => config["BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";

    // ── Register ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Register a new account with email and password.
    /// A verification link is sent to the provided email address.
    /// The account is inactive until the email is verified.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register([FromBody] WebRegisterRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!abuseDetection.CheckRegisterRate(ip))
            return StatusCode(429, new { error = "Too many registration attempts. Try again later." });

        if (request.Password != request.PasswordConfirm)
            return BadRequest(new { error = "Passwords do not match." });

        var emailHash = crypto.HashEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.EmailHash == emailHash))
            return Conflict(new { error = "An account with this email already exists." });

        var token    = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                           .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expiry   = DateTime.UtcNow.AddHours(24);

        var user = new User
        {
            EmailHash          = emailHash,
            PasswordHash       = passwordService.HashPassword(request.Password),
            IsEmailVerified    = false,
            VerificationToken  = token,
            VerificationExpiry = expiry,
            CreatedAt          = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var verifyUrl = $"{BaseUrl}/auth/verify?token={Uri.EscapeDataString(token)}";
        await emailService.SendVerificationEmailAsync(request.Email, verifyUrl);

        return Ok(new { message = "Registration successful. Please check your email to verify your account." });
    }

    // ── Verify Email ──────────────────────────────────────────────────────────

    /// <summary>Verify email address using the token sent by email. Redirects to the home page.</summary>
    [HttpGet("verify")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Redirect("/?verified=invalid");

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.VerificationToken == token && u.VerificationExpiry > DateTime.UtcNow);

        if (user == null)
            return Redirect("/?verified=invalid");

        user.IsEmailVerified    = true;
        user.VerificationToken  = null;
        user.VerificationExpiry = null;
        await db.SaveChangesAsync();

        return Redirect("/?verified=1");
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Log in with email and password. Sets a session cookie on success.
    /// The account must be email-verified before login is allowed.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(WebLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] WebLoginRequest request)
    {
        var emailHash = crypto.HashEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == emailHash);

        // Generic error to prevent user enumeration
        if (user == null || !passwordService.VerifyPassword(request.Password, user.PasswordHash))
            return Unauthorized(new { error = "Invalid email or password." });

        if (!user.IsEmailVerified)
            return Unauthorized(new { error = "Please verify your email address before logging in." });

        if (!user.IsActive)
            return Unauthorized(new { error = "This account has been deactivated." });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, request.Email),
            new(ClaimTypes.Name, user.Name),
            new("role", user.Role.ToString()),
        };

        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var authProps = new AuthenticationProperties
        {
            IsPersistent = request.RememberMe,
            ExpiresUtc   = request.RememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(12)
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);

        return Ok(new WebLoginResponse
        {
            Id   = user.Id,
            Name = user.Name,
            Role = user.Role.ToString()
        });
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    /// <summary>Log out — clears the session cookie.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { message = "Logged out." });
    }
}
