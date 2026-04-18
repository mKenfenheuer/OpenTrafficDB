using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDBModels.DTOs;

public class WebRegisterRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string PasswordConfirm { get; set; } = string.Empty;
}

public class WebLoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; } = false;
}

public class WebLoginResponse
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class WebProfileResponse
{
    public int      Id                      { get; set; }
    public string   Name                    { get; set; } = string.Empty;
    public string   Email                   { get; set; } = string.Empty;
    public string   Role                    { get; set; } = string.Empty;
    public long     TotalContributions      { get; set; }
    public long     ContributionsToNextRank { get; set; }
    public DateTime CreatedAt               { get; set; }
}

public class UpdateNameRequest
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(100)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    public string NewPasswordConfirm { get; set; } = string.Empty;
}

public class ChangeEmailRequest
{
    [Required]
    public string Password { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(255)]
    public string NewEmail { get; set; } = string.Empty;
}

public class ForgotPasswordRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    [Required]
    public string Token { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string PasswordConfirm { get; set; } = string.Empty;
}
