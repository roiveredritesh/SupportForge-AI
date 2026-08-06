using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileProjectMembershipRepository : IProjectMembershipRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileProjectMembershipRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "project-memberships.json");
    }

    private async Task<List<ProjectMembership>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new();
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<ProjectMembership>>(stream, cancellationToken: ct) ?? new();
    }

    private Task WriteAllAsync(List<ProjectMembership> all, CancellationToken ct) =>
        File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task<bool> IsMemberAsync(string userId, string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Any(m => m.UserId == userId && m.ProjectId == projectId);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetProjectIdsForUserAsync(string userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Where(m => m.UserId == userId).Select(m => m.ProjectId).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(string userId, string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            if (all.Any(m => m.UserId == userId && m.ProjectId == projectId)) return;
            all.Add(new ProjectMembership(projectId, userId, DateTimeOffset.UtcNow));
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(m => m.ProjectId == projectId);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }
}
