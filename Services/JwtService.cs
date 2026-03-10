using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OpenTrafficDB.Models;

namespace OpenTrafficDB.Services;

public class JwtService(IConfiguration config)
{
    private readonly string _key = config["Jwt:Key"]
        ?? throw new InvalidOperationException("Jwt:Key is not configured.");
    private readonly string _issuer = config["Jwt:Issuer"] ?? "OpenTrafficDB";
    private readonly string _audience = config["Jwt:Audience"] ?? "OpenTrafficDB";
    private readonly int _expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var m) ? m : 60;
    private readonly string _pepper = config["Security:IpHashPepper"] ?? "default-pepper";

    public string GenerateRawApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public string HashApiKey(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    public string GetKeyPrefix(string rawKey) =>
        rawKey.Length >= 8 ? rawKey[..8] : rawKey;

    public string HashEmail(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))).ToLowerInvariant();

    public string HashIp(string ip) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip + _pepper))).ToLowerInvariant();

    public (string token, DateTime expiresAt) GenerateJwt(ApiKey apiKey)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(_expiryMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, apiKey.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, apiKey.Name),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("keyHash", apiKey.KeyHash),
            new Claim("isAdmin", apiKey.IsAdmin ? "true" : "false"),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
