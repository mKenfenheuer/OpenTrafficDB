using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
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

    /// <summary>
    /// Verify email address using the token sent by email. Handles both initial registration
    /// verification and email-change confirmation. Redirects to the home page.
    /// </summary>
    [HttpGet("verify")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Redirect("/Home/Login?verified=invalid");

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.VerificationToken == token && u.VerificationExpiry > DateTime.UtcNow);

        if (user == null)
            return Redirect("/Home/Login?verified=invalid");

        if (user.PendingEmailHash != null)
        {
            // Email-change confirmation: swap to the new email hash
            user.EmailHash         = user.PendingEmailHash;
            user.PendingEmailHash  = null;
            user.VerificationToken  = null;
            user.VerificationExpiry = null;
            await db.SaveChangesAsync();
            return Redirect("/Home/Profile?emailchanged=1");
        }

        // Initial registration verification
        user.IsEmailVerified    = true;
        user.VerificationToken  = null;
        user.VerificationExpiry = null;
        await db.SaveChangesAsync();

        return Redirect("/Home/Login?verified=1");
    }

    // ── Forgot Password ───────────────────────────────────────────────────────

    /// <summary>
    /// Request a password reset link. Always returns 200 to prevent user enumeration.
    /// If the email matches an account a reset link is sent.
    /// </summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!abuseDetection.CheckPasswordResetRate(ip))
            return StatusCode(429, new { error = "Too many requests. Try again later." });

        var emailHash = crypto.HashEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailHash == emailHash && u.IsActive);

        if (user != null && user.IsEmailVerified)
        {
            var token  = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                             .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            user.PasswordResetToken  = token;
            user.PasswordResetExpiry = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();

            var resetUrl = $"{BaseUrl}/Home/ResetPassword?token={Uri.EscapeDataString(token)}";
            await emailService.SendPasswordResetEmailAsync(request.Email, resetUrl);
        }

        return Ok(new { message = "If an account with that email exists, a password reset link has been sent." });
    }

    // ── Reset Password ────────────────────────────────────────────────────────

    /// <summary>Reset password using a token received by email.</summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        if (request.Password != request.PasswordConfirm)
            return BadRequest(new { error = "Passwords do not match." });

        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequest(new { error = "Invalid or expired token." });

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == request.Token && u.PasswordResetExpiry > DateTime.UtcNow);

        if (user == null)
            return BadRequest(new { error = "Invalid or expired reset link. Please request a new one." });

        user.PasswordHash        = passwordService.HashPassword(request.Password);
        user.PasswordResetToken  = null;
        user.PasswordResetExpiry = null;
        await db.SaveChangesAsync();

        return Ok(new { message = "Password reset successfully. You can now log in with your new password." });
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
