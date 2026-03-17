namespace OpenTrafficDB.Shared.Models;

public class ApiKey
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 of the raw API key</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First 8 chars of the raw key, used for fast lookup before full hash compare</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Optional human-readable label to identify this key, e.g. "Home PC" or "Phone"</summary>
    public string Label { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    public ICollection<SpeedObservation> Observations { get; set; } = [];
}
