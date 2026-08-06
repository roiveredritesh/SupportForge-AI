using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileDeadLetterRepository : IDeadLetterRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileDeadLetterRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "dead-letters.json");
    }

    private async Task<List<DeadLetterEntry>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new();
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<DeadLetterEntry>>(stream, cancellationToken: ct) ?? new();
    }

    private Task WriteAllAsync(List<DeadLetterEntry> all, CancellationToken ct) =>
        File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.Add(entry);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> GetByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Where(e => e.ProjectId == projectId).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<DeadLetterEntry?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.FirstOrDefault(e => e.Id == id);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(e => e.Id == id);
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
            all.RemoveAll(e => e.ProjectId == projectId);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }
}
