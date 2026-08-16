using System.Text.Json;

namespace SupportForge.Core;

public sealed class JsonFileContentHashRepository : IContentHashRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileContentHashRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "content-hashes.json");
    }

    private static string Key(string projectId, string sourceRef) => $"{projectId}:{sourceRef}";

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new();
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct) ?? new();
    }

    private Task WriteAllAsync(Dictionary<string, string> all, CancellationToken ct) =>
        File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);

    public async Task<string?> GetHashAsync(string projectId, string sourceRef, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            return all.TryGetValue(Key(projectId, sourceRef), out var hash) ? hash : null;
        }
        finally { _lock.Release(); }
    }

    public async Task SetHashAsync(string projectId, string sourceRef, string hash, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all[Key(projectId, sourceRef)] = hash;
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
            var prefix = $"{projectId}:";
            foreach (var key in all.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                all.Remove(key);
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<string>> GetSourceRefsAsync(string projectId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            var prefix = $"{projectId}:";
            return all.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..]).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string projectId, string sourceRef, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAllAsync(ct);
            all.Remove(Key(projectId, sourceRef));
            await WriteAllAsync(all, ct);
        }
        finally { _lock.Release(); }
    }
}
