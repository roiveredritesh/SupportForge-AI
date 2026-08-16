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
        var org = new Org { Id = "org1", Name = "Acme", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" };
        await _repo.UpsertAsync(org);

        var found = await _repo.GetByIdAsync("org1");

        Assert.NotNull(found);
        Assert.Equal("Acme", found!.Name);
    }

    [Fact]
    public async Task UpsertAsync_ExistingId_ReplacesInsteadOfDuplicating()
    {
        await _repo.UpsertAsync(new Org { Id = "org1", Name = "Old Name", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });
        await _repo.UpsertAsync(new Org { Id = "org1", Name = "New Name", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });

        var all = await _repo.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("New Name", all[0].Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOrg()
    {
        await _repo.UpsertAsync(new Org { Id = "org1", Name = "Acme", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });

        await _repo.DeleteAsync("org1");

        Assert.Null(await _repo.GetByIdAsync("org1"));
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _repo.GetByIdAsync("missing"));
    }

    // Sprint 3 (U13): Org.Connections round-trips through the same whole-object JSON
    // serialization the rest of Org already relies on -- no per-field wiring needed.
    [Fact]
    public async Task UpsertAsync_WithMcpConnection_RoundTripsConnection()
    {
        var org = new Org
        {
            Id = "org1",
            Name = "Acme",
            ContactPerson = "Jane Doe",
            ContactNumber = "555-0100",
            Industry = "Software",
            Connections = [new McpConnection { ServerType = "github", Credential = "ghp_secret", EnabledTools = ["list_commits"] }],
        };

        await _repo.UpsertAsync(org);
        var found = await _repo.GetByIdAsync("org1");

        var connection = Assert.Single(found!.Connections);
        Assert.Equal("github", connection.ServerType);
        Assert.Equal("ghp_secret", connection.Credential);
        Assert.Equal(["list_commits"], connection.EnabledTools);
    }

    [Fact]
    public async Task UpsertAsync_WithMultipleMcpConnections_RoundTripsIndependently()
    {
        var org = new Org
        {
            Id = "org1",
            Name = "Acme",
            ContactPerson = "Jane Doe",
            ContactNumber = "555-0100",
            Industry = "Software",
            Connections =
            [
                new McpConnection { ServerType = "github", Credential = "ghp_secret", EnabledTools = ["list_commits"] },
                new McpConnection { ServerType = "jira", Credential = "jira_token", EnabledTools = ["create_issue"] },
            ],
        };

        await _repo.UpsertAsync(org);
        var found = await _repo.GetByIdAsync("org1");

        Assert.Equal(2, found!.Connections.Count);
        var github = found.Connections.Single(c => c.ServerType == "github");
        var jira = found.Connections.Single(c => c.ServerType == "jira");
        Assert.Equal("ghp_secret", github.Credential);
        Assert.Equal("jira_token", jira.Credential);
    }
}
