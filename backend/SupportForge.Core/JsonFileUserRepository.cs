using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileUserRepository : IUserRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileUserRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "users.json");
    }

    public async Task<IReadOnlyList<AppUser>> GetAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<AppUser>();

        await _lock.WaitAsync(ct);
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<AppUser>>(stream, cancellationToken: ct) ?? new();
        }
        finally { _lock.Release(); }
    }

    public async Task<AppUser?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(u => u.Id == id);
    }

    public async Task UpsertAsync(AppUser user, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<AppUser>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<AppUser>();

            all.RemoveAll(u => u.Id == user.Id);
            all.Add(user);

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
                ? JsonSerializer.Deserialize<List<AppUser>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<AppUser>();

            all.RemoveAll(u => u.Id == id);

            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }
}
