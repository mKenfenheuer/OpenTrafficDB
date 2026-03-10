using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.Data;
using OpenTrafficDB.Models;

namespace OpenTrafficDB.Services;

/// <summary>
/// Singleton service tracking rate-limit windows and blocked IPs in memory.
/// Uses IServiceScopeFactory for DB access since it is a singleton.
/// </summary>
public class AbuseDetectionService(
    IServiceScopeFactory scopeFactory,
    JwtService jwtService,
    IConfiguration config,
    ILogger<AbuseDetectionService> logger)
{
    private readonly int _submitIpLimit = int.TryParse(config["Security:SubmitRateLimitPerMinutePerIp"], out var v1) ? v1 : 120;
    private readonly int _submitKeyLimit = int.TryParse(config["Security:SubmitRateLimitPerMinutePerKey"], out var v2) ? v2 : 200;
    private readonly int _readIpLimit = int.TryParse(config["Security:ReadRateLimitPerMinutePerIp"], out var v3) ? v3 : 30;
    private readonly int _readKeyLimit = int.TryParse(config["Security:ReadRateLimitPerMinutePerKey"], out var v4) ? v4 : 60;
    private readonly int _registerIpLimit = int.TryParse(config["Security:RegisterRateLimitPerHourPerIp"], out var v5) ? v5 : 5;

    // In-memory rate windows: key → list of request timestamps
    private readonly ConcurrentDictionary<string, List<DateTime>> _windows = new();

    // Blocked IP hash cache — refreshed from DB periodically
    private readonly ConcurrentDictionary<string, byte> _blockedCache = new();
    private DateTime _cacheLastRefresh = DateTime.MinValue;

    public async Task SeedFromDbAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var hashes = await db.BlockedIps
            .Where(b => b.ExpiresAt == null || b.ExpiresAt > now)
            .Select(b => b.IpHash)
            .ToListAsync();

        _blockedCache.Clear();
        foreach (var h in hashes) _blockedCache[h] = 0;
        _cacheLastRefresh = now;

        logger.LogInformation("Loaded {Count} blocked IPs from database.", hashes.Count);
    }

    public async Task<bool> IsIpBlockedAsync(string ip)
    {
        // Refresh cache every 5 minutes
        if (DateTime.UtcNow - _cacheLastRefresh > TimeSpan.FromMinutes(5))
            await SeedFromDbAsync();

        return _blockedCache.ContainsKey(jwtService.HashIp(ip));
    }

    public async Task<bool> CheckSubmitRateAsync(string ip, string apiKeyId)
    {
        if (!Allow($"submit:ip:{ip}", 60, _submitIpLimit))
        {
            await AutoBlockAsync(ip, "Submit rate limit exceeded");
            return false;
        }
        if (!Allow($"submit:key:{apiKeyId}", 60, _submitKeyLimit))
            return false;

        return true;
    }

    public bool CheckReadRate(string ip, string apiKeyId)
    {
        return Allow($"read:ip:{ip}", 60, _readIpLimit)
            && Allow($"read:key:{apiKeyId}", 60, _readKeyLimit);
    }

    public bool CheckRegisterRate(string ip)
    {
        return Allow($"register:ip:{ip}", 3600, _registerIpLimit);
    }

    public async Task ManualBlockAsync(string ip, string reason, DateTime? expiresAt)
    {
        var ipHash = jwtService.HashIp(ip);
        _blockedCache[ipHash] = 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.BlockedIps.AnyAsync(b => b.IpHash == ipHash))
        {
            db.BlockedIps.Add(new BlockedIp
            {
                IpHash = ipHash,
                Reason = reason,
                IsManual = true,
                BlockedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task UnblockAsync(int blockedIpId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.BlockedIps.FindAsync(blockedIpId);
        if (entry == null) return;

        _blockedCache.TryRemove(entry.IpHash, out _);
        db.BlockedIps.Remove(entry);
        await db.SaveChangesAsync();
    }

    private async Task AutoBlockAsync(string ip, string reason)
    {
        var ipHash = jwtService.HashIp(ip);
        if (_blockedCache.ContainsKey(ipHash)) return;

        _blockedCache[ipHash] = 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.BlockedIps.AnyAsync(b => b.IpHash == ipHash))
        {
            db.BlockedIps.Add(new BlockedIp
            {
                IpHash = ipHash,
                Reason = reason,
                IsManual = false,
                BlockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            });
            try { await db.SaveChangesAsync(); }
            catch { /* ignore race condition — cache already updated */ }
        }

        logger.LogWarning("Auto-blocked IP (hash {Hash}) for: {Reason}", ipHash[..8], reason);
    }

    private bool Allow(string key, int windowSeconds, int maxRequests)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-windowSeconds);
        var window = _windows.GetOrAdd(key, _ => []);

        lock (window)
        {
            window.RemoveAll(t => t < cutoff);
            if (window.Count >= maxRequests) return false;
            window.Add(now);
            return true;
        }
    }
}
