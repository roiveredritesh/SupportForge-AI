using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileTokenUsageRepository : ITokenUsageRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileTokenUsageRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "token_usage.json");
    }

    public async Task AddAsync(TokenUsageEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<TokenUsageEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<TokenUsageEntry>();

            all.Add(entry);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all), ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> GetTotalForProjectAsync(string projectId, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return 0;

        await _lock.WaitAsync(ct);
        try
        {
            var all = JsonSerializer.Deserialize<List<TokenUsageEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new();
            return all.Where(e => e.ProjectId == projectId).Sum(e => e.TotalTokens);
        }
        finally { _lock.Release(); }
    }

    public async Task<Dictionary<string, int>> GetTotalsBySourceForProjectAsync(string projectId, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return new();

        await _lock.WaitAsync(ct);
        try
        {
            var all = JsonSerializer.Deserialize<List<TokenUsageEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new();
            return all.Where(e => e.ProjectId == projectId)
                .GroupBy(e => e.Source)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.TotalTokens));
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<TokenUsageEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<TokenUsageEntry>();

            all.RemoveAll(e => e.ProjectId == projectId);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all), ct);
        }
        finally { _lock.Release(); }
    }
}
