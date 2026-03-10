using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.Models.DTOs;

public class RegisterRequest
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;
}

public class RegisterResponse
{
    /// <summary>Raw API key — show this once and never again. Store it securely.</summary>
    public string ApiKey { get; set; } = string.Empty;
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
}
