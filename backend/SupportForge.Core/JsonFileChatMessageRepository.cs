using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileChatMessageRepository : IChatMessageRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileChatMessageRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "messages.json");
    }

    private async Task<List<ChatMessage>> ReadAllAsync(CancellationToken ct)
        => File.Exists(_filePath)
            ? JsonSerializer.Deserialize<List<ChatMessage>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
            : new List<ChatMessage>();

    private Task WriteAllAsync(List<ChatMessage> all, CancellationToken ct)
        => File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task<IReadOnlyList<ChatMessage>> GetByConversationIdAsync(string conversationId, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<ChatMessage>();

        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.Where(m => m.ConversationId == conversationId).OrderBy(m => m.CreatedAt).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(ChatMessage message, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.Add(message);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByConversationIdAsync(string conversationId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(m => m.ConversationId == conversationId);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByConversationIdsAsync(IReadOnlyCollection<string> conversationIds, CancellationToken ct = default)
    {
        if (conversationIds.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.RemoveAll(m => conversationIds.Contains(m.ConversationId));
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }
}
