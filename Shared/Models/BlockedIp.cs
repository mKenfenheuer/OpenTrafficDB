namespace OpenTrafficDB.App.Models;

public class BlockedIp
{
    public int Id { get; set; }
    public string IpHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool IsManual { get; set; } = false;
    public DateTime BlockedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}
