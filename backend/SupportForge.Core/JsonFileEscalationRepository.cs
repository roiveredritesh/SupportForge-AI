using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

// U19: mirrors JsonFileConversationRepository's read-all/write-all-under-lock shape.
public sealed class JsonFileEscalationRepository : IEscalationRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileEscalationRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "escalations.json");
    }

    private async Task<List<Escalation>> ReadAllAsync(CancellationToken ct)
        => File.Exists(_filePath)
            ? JsonSerializer.Deserialize<List<Escalation>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
            : new List<Escalation>();

    private Task WriteAllAsync(List<Escalation> all, CancellationToken ct)
        => File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task<IReadOnlyList<Escalation>> GetAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<Escalation>();

        await _lock.WaitAsync(ct);
        try { return await ReadAllAsync(ct); }
        finally { _lock.Release(); }
    }

    public async Task<Escalation?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return null;

        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.FirstOrDefault(e => e.Id == id);
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertAsync(Escalation escalation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(e => e.Id == escalation.Id);
            all.Add(escalation);
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
