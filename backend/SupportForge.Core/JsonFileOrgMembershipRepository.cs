using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileOrgMembershipRepository : IOrgMembershipRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileOrgMembershipRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "org-memberships.json");
    }

    private async Task<List<OrgMembership>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new();
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<OrgMembership>>(stream, cancellationToken: ct) ?? new();
    }

    private Task WriteAllAsync(List<OrgMembership> all, CancellationToken ct) =>
        File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task<bool> IsMemberAsync(string userId, string orgId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Any(m => m.UserId == userId && m.OrgId == orgId);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetOrgIdsForUserAsync(string userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Where(m => m.UserId == userId).Select(m => m.OrgId).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetUserIdsForOrgAsync(string orgId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Where(m => m.OrgId == orgId).Select(m => m.UserId).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(string userId, string orgId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            if (all.Any(m => m.UserId == userId && m.OrgId == orgId)) return;
            all.Add(new OrgMembership(orgId, userId, DateTimeOffset.UtcNow));
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByOrgIdAsync(string orgId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(m => m.OrgId == orgId);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }
}
