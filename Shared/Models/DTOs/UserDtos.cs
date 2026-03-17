using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.App.Models.DTOs;

public class UserProfileResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public long TotalContributions { get; set; }
    public DateTime CreatedAt { get; set; }
    public long ContributionsToNextRank { get; set; }
}

public class ApiKeyResponse
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsCurrent { get; set; }
}

public class CreateKeyRequest
{
    [MaxLength(100)]
    public string Label { get; set; } = "Default";
}

public class CreateKeyResponse
{
    public string ApiKey { get; set; } = string.Empty;
    public int KeyId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class UpdateKeyLabelRequest
{
    [Required, MaxLength(100)]
    public string Label { get; set; } = string.Empty;
}
