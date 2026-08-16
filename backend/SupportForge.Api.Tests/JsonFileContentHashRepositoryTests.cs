using SupportForge.Core;
using Xunit;

namespace SupportForge.Api.Tests;

public class JsonFileContentHashRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileContentHashRepository _repo;

    public JsonFileContentHashRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _repo = new JsonFileContentHashRepository(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetHashAsync_NoEntry_ReturnsNull()
    {
        Assert.Null(await _repo.GetHashAsync("proj1", "file.md"));
    }

    [Fact]
    public async Task SetHashAsync_Then_GetHashAsync_ReturnsStoredHash()
    {
        await _repo.SetHashAsync("proj1", "file.md", "hash1");

        Assert.Equal("hash1", await _repo.GetHashAsync("proj1", "file.md"));
    }

    [Fact]
    public async Task GetSourceRefsAsync_ReturnsOnlyThatProjectsRefs()
    {
        await _repo.SetHashAsync("proj1", "a.md", "h1");
        await _repo.SetHashAsync("proj1", "b.md", "h2");
        await _repo.SetHashAsync("proj2", "c.md", "h3");

        var refs = await _repo.GetSourceRefsAsync("proj1");

        Assert.Equal(new[] { "a.md", "b.md" }, refs.OrderBy(x => x));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyThatEntry()
    {
        await _repo.SetHashAsync("proj1", "a.md", "h1");
        await _repo.SetHashAsync("proj1", "b.md", "h2");

        await _repo.DeleteAsync("proj1", "a.md");

        Assert.Null(await _repo.GetHashAsync("proj1", "a.md"));
        Assert.Equal("h2", await _repo.GetHashAsync("proj1", "b.md"));
    }

    [Fact]
    public async Task DeleteByProjectIdAsync_RemovesAllHashesForProject()
    {
        await _repo.SetHashAsync("proj1", "a.md", "h1");
        await _repo.SetHashAsync("proj2", "b.md", "h2");

        await _repo.DeleteByProjectIdAsync("proj1");

        Assert.Empty(await _repo.GetSourceRefsAsync("proj1"));
        Assert.Single(await _repo.GetSourceRefsAsync("proj2"));
    }
}
