using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OpenTrafficDB.Shared.Services;

/// <summary>
/// Issues short-lived JWTs for inter-service communication.
/// Workers validate these tokens to authenticate requests forwarded from the App.
/// </summary>
public class InternalJwtService(IConfiguration config)
{
    private readonly SymmetricSecurityKey _key = new(
        Encoding.UTF8.GetBytes(
            config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required")));

    /// <summary>
    /// Creates a 5-minute internal JWT carrying the forwarded user context.
    /// Workers validate iss=opentrafficdb-app, aud=opentrafficdb-internal.
    /// </summary>
    public string CreateInternalToken(int userId, string keyId, string ipHash)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("keyId",  keyId),
            new Claim("ipHash", ipHash)
        };

        var token = new JwtSecurityToken(
            issuer:             "opentrafficdb-app",
            audience:           "opentrafficdb-internal",
            claims:             claims,
            expires:            DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
