namespace OpenTrafficDB.Models;

public class ApiKey
{
    public int Id { get; set; }

    /// <summary>SHA-256 of the raw API key</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First 8 chars of the raw key, used for fast lookup before full hash compare</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of lowercase email — never store raw email</summary>
    public string EmailHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    public long TotalContributions { get; set; } = 0;

    public ICollection<SpeedObservation> Observations { get; set; } = [];
}
