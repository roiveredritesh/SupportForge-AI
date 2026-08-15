using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class JsonFileOrgRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileOrgRepository _repo;

    public JsonFileOrgRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _repo = new JsonFileOrgRepository(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetAllAsync_NoFile_ReturnsEmpty()
    {
        var all = await _repo.GetAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task UpsertAsync_Then_GetByIdAsync_ReturnsOrg()
    {
        var org = new Org { Id = "org1", Name = "Acme" };
        await _repo.UpsertAsync(org);

        var found = await _repo.GetByIdAsync("org1");

        Assert.NotNull(found);
        Assert.Equal("Acme", found!.Name);
    }

    [Fact]
    public async Task UpsertAsync_ExistingId_ReplacesInsteadOfDuplicating()
    {
        await _repo.UpsertAsync(new Org { Id = "org1", Name = "Old Name" });
        await _repo.UpsertAsync(new Org { Id = "org1", Name = "New Name" });

        var all = await _repo.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("New Name", all[0].Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOrg()
    {
        await _repo.UpsertAsync(new Org { Id = "org1", Name = "Acme" });

        await _repo.DeleteAsync("org1");

        Assert.Null(await _repo.GetByIdAsync("org1"));
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _repo.GetByIdAsync("missing"));
    }
}
