using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;

namespace OpenTrafficDB.Services;

/// <summary>
/// Authenticates requests using a raw API key supplied as:
///   Authorization: ApiKey &lt;key&gt;
/// Sets claims: sub (User.Id), role, keyId (ApiKey.Id).
/// Only valid for active keys whose owner is active and email-verified.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AppDbContext db,
    CryptoService crypto) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Bearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var header = authHeader.ToString();
        if (!header.StartsWith($"{SchemeName} ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var rawKey = header[$"{SchemeName} ".Length..].Trim();
        if (string.IsNullOrEmpty(rawKey))
            return AuthenticateResult.Fail("Missing API key value.");

        var prefix = crypto.GetKeyPrefix(rawKey);
        var hash   = crypto.HashApiKey(rawKey);

        var apiKey = await db.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k =>
                k.KeyPrefix == prefix &&
                k.KeyHash   == hash   &&
                k.IsActive            &&
                k.User.IsActive       &&
                k.User.IsEmailVerified);

        if (apiKey == null)
            return AuthenticateResult.Fail("Invalid or deactivated API key.");

        // Update last-used timestamp without loading a full change-tracking unit of work
        await db.ApiKeys
            .Where(k => k.Id == apiKey.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow));

        var claims = new[]
        {
            new Claim("sub",   apiKey.User.Id.ToString()),
            new Claim("role",  apiKey.User.Role == UserRole.User ? UserRole.User.ToString() : UserRole.Contributor.ToString()),
            new Claim("keyId", apiKey.Id.ToString()),
        };

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
