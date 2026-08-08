using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileOrgRepository : IOrgRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileOrgRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "orgs.json");
    }

    public async Task<IReadOnlyList<Org>> GetAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<Org>();

        await _lock.WaitAsync(ct);
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<Org>>(stream, cancellationToken: ct) ?? new();
        }
        finally { _lock.Release(); }
    }

    public async Task<Org?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(o => o.Id == id);
    }

    public async Task UpsertAsync(Org org, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<Org>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<Org>();

            all.RemoveAll(o => o.Id == org.Id);
            all.Add(org);

            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<Org>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<Org>();

            all.RemoveAll(o => o.Id == id);

            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }
}
