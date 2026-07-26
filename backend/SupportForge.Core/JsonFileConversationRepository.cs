using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileConversationRepository : IConversationRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileConversationRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "conversations.json");
    }

    private async Task<List<Conversation>> ReadAllAsync(CancellationToken ct)
        => File.Exists(_filePath)
            ? JsonSerializer.Deserialize<List<Conversation>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
            : new List<Conversation>();

    private Task WriteAllAsync(List<Conversation> all, CancellationToken ct)
        => File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task<IReadOnlyList<Conversation>> GetByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<Conversation>();

        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Where(c => c.ProjectId == projectId).OrderByDescending(c => c.UpdatedAt).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<Conversation?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return null;

        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.FirstOrDefault(c => c.Id == id);
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertAsync(Conversation conversation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(c => c.Id == conversation.Id);
            all.Add(conversation);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(c => c.Id == id);
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
            all.RemoveAll(c => c.ProjectId == projectId);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }
}
