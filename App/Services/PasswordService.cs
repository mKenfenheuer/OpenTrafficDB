using System.Security.Cryptography;
using System.Text;

namespace OpenTrafficDB.App.Services;

/// <summary>PBKDF2-SHA256 password hashing. Salt and hash stored as base64(salt).base64(hash).</summary>
public class PasswordService
{
    private const int Iterations  = 100_000;
    private const int HashLength  = 32; // 256 bits
    private const int SaltLength  = 16; // 128 bits

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt, Iterations, HashAlgorithmName.SHA256, HashLength);

        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string stored)
    {
        var dot = stored.IndexOf('.');
        if (dot < 0) return false;

        byte[] salt, expected;
        try
        {
            salt     = Convert.FromBase64String(stored[..dot]);
            expected = Convert.FromBase64String(stored[(dot + 1)..]);
        }
        catch { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt, Iterations, HashAlgorithmName.SHA256, HashLength);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
