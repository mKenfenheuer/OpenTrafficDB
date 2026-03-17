using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.App.Models.DTOs;

public class RegisterRequest
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Optional label for the first API key, e.g. "Default"</summary>
    [MaxLength(100)]
    public string KeyLabel { get; set; } = "Default";
}

public class RegisterResponse
{
    /// <summary>Raw API key — shown once. Store it securely.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public int KeyId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TokenRequest
{
    [Required]
    public string ApiKey { get; set; } = string.Empty;
}

public class TokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public DateTime ExpiresAt { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
