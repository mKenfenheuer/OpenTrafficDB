using System.ComponentModel.DataAnnotations;

namespace OpenTrafficDB.Shared.Models.DTOs;

public class UserProfileResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public long TotalContributions { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Contributions needed to reach Contributor rank (0 if already Contributor or Admin).</summary>
    public long ContributionsToNextRank { get; set; }
}

public class ApiKeyResponse
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>True if this is the key that issued the current JWT.</summary>
    public bool IsCurrent { get; set; }
}

public class CreateKeyRequest
{
    [MaxLength(100)]
    public string Label { get; set; } = "Default";
}

public class CreateKeyResponse
{
    /// <summary>New raw API key — shown once. Store it securely.</summary>
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
