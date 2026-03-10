using System.Security.Cryptography;
using System.Text;

namespace OpenTrafficDB.Services;

/// <summary>Hashing and API key utilities (no JWT — see ApiKeyAuthenticationHandler for auth).</summary>
public class CryptoService(IConfiguration config)
{
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
    public string HashTripId(string tripId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tripId.Trim().ToLowerInvariant()))).ToLowerInvariant();

    public string HashIp(string ip) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip + _pepper))).ToLowerInvariant();
}
