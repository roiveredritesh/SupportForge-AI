using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileFeedbackRepository : IFeedbackRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileFeedbackRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "feedback.json");
    }

    public async Task AddAsync(FeedbackEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<FeedbackEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<FeedbackEntry>();

            all.Add(entry);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<FeedbackEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<FeedbackEntry>();

            all.RemoveAll(f => f.ProjectId == projectId);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }
}
