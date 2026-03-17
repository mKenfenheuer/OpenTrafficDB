namespace OpenTrafficDB.App.Models;

public class BlockedIp
{
    public int Id { get; set; }

    /// <summary>SHA-256 + pepper hash of the IP address — never store raw IP</summary>
    public string IpHash { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>True = blocked by an admin; False = auto-blocked by abuse detection</summary>
    public bool IsManual { get; set; } = false;

    public DateTime BlockedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null = permanent block</summary>
    public DateTime? ExpiresAt { get; set; }
}
