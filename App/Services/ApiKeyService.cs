using Microsoft.EntityFrameworkCore;
using OpenTrafficDBData;
using OpenTrafficDBModels;
using OpenTrafficDBModels.DTOs;
using OpenTrafficDBServices;

namespace OpenTrafficDBServices;

/// <summary>Shared API key management used by both JWT and cookie-authenticated controllers.</summary>
public class ApiKeyService(AppDbContext db, CryptoService crypto)
{
    public async Task<List<ApiKeyResponse>> GetKeysAsync(int userId, int? currentKeyId = null)
    {
        return await db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyResponse
            {
                Id          = k.Id,
                Label       = k.Label,
                IsActive    = k.IsActive,
                CreatedAt   = k.CreatedAt,
                LastUsedAt  = k.LastUsedAt,
                IsCurrent   = currentKeyId.HasValue && k.Id == currentKeyId.Value
            })
            .ToListAsync();
    }

    public async Task<(string rawKey, ApiKey key)> CreateKeyAsync(int userId, string label)
    {
        var rawKey = crypto.GenerateRawApiKey();
        var trimmed = string.IsNullOrWhiteSpace(label) ? "Default" : label.Trim();

        var apiKey = new ApiKey
        {
            UserId    = userId,
            KeyHash   = crypto.HashApiKey(rawKey),
            KeyPrefix = crypto.GetKeyPrefix(rawKey),
            Label     = trimmed,
            CreatedAt = DateTime.UtcNow
        };

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();

        return (rawKey, apiKey);
    }

    /// <summary>
    /// Deactivates a key owned by userId.
    /// currentKeyId: the key that issued the current session (may not revoke itself).
    /// Returns (success, error message).
    /// </summary>
    public async Task<(bool ok, string? error)> RevokeKeyAsync(int userId, int keyId, int? currentKeyId = null)
    {
        if (currentKeyId.HasValue && keyId == currentKeyId.Value)
            return (false, "You cannot revoke the key you are currently using.");

        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);
        if (key == null) return (false, null); // caller should 404

        key.IsActive = false;
        await db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>
    /// Updates the label of a key owned by userId.
    /// Returns true if found and updated, false if not found.
    /// </summary>
    public async Task<bool> UpdateLabelAsync(int userId, int keyId, string label)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId);
        if (key == null) return false;

        key.Label = label.Trim();
        await db.SaveChangesAsync();
        return true;
    }
}
