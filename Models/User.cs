namespace OpenTrafficDB.Models;

public class User
{
    public int Id { get; set; }

    /// <summary>Display name — set via profile management after registration, empty initially</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of lowercase email — never store raw email</summary>
    public string EmailHash { get; set; } = string.Empty;

    /// <summary>PBKDF2-SHA256 password hash: base64(salt).base64(hash)</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsEmailVerified { get; set; } = false;

    /// <summary>Single-use token for email verification (stored as-is, 256-bit entropy)</summary>
    public string? VerificationToken { get; set; }

    public DateTime? VerificationExpiry { get; set; }

    public bool IsActive { get; set; } = true;

    public UserRole Role { get; set; } = UserRole.User;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Total valid observations submitted across all API keys</summary>
    public long TotalContributions { get; set; } = 0;

    public ICollection<ApiKey> ApiKeys { get; set; } = [];
}
