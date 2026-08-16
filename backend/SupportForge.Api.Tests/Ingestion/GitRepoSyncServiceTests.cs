using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Code;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GitRepoSyncServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "org-repo-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void CloneOrPull_ClonesFreshRepo_WhenLocalPathDoesNotExist()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), "remote-" + Guid.NewGuid());
        var localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid());
        Repository.Init(remoteDir, isBare: true);

        var seedDir = Path.Combine(Path.GetTempPath(), "seed-" + Guid.NewGuid());
        Repository.Clone(remoteDir, seedDir);
        File.WriteAllText(Path.Combine(seedDir, "readme.md"), "hello");
        using (var seedRepo = new Repository(seedDir))
        {
            Commands.Stage(seedRepo, "*");
            var sig = new Signature("test", "test@test.com", DateTimeOffset.Now);
            seedRepo.Commit("initial commit", sig, sig);
            seedRepo.Network.Push(seedRepo.Branches["master"]);
        }

        var sut = new GitRepoSyncService(new ConfigurationBuilder().Build());
        sut.CloneOrPull(remoteDir, localDir, "master");

        Assert.True(File.Exists(Path.Combine(localDir, "readme.md")));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        System.Threading.Thread.Sleep(100);

        try { Directory.Delete(remoteDir, recursive: true); } catch { }
        try { Directory.Delete(localDir, recursive: true); } catch { }
        try { Directory.Delete(seedDir, recursive: true); } catch { }
    }

    private static IConfiguration ConfigWithGlobalToken(string token) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:Token"] = token }).Build();

    // Sprint 3 (U15): precedence tests for ResolveTokenAsync (internal, see InternalsVisibleTo) --
    // the actual credential a clone/pull uses, without needing a real authenticated git remote.
    [Fact]
    public async Task ResolveTokenAsync_OrgWithNoMcpConnection_FallsBackToGlobalConfigUnchanged()
    {
        var orgs = new JsonFileOrgRepository(_tempDir);
        await orgs.UpsertAsync(new Org { Id = "org1", Name = "Acme", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" }); // no Connections

        var sut = new GitRepoSyncService(ConfigWithGlobalToken("global-token"), orgs);

        var token = await sut.ResolveTokenAsync("org1");

        Assert.Equal("global-token", token);
    }

    [Fact]
    public async Task ResolveTokenAsync_NoOrgId_FallsBackToGlobalConfigUnchanged()
    {
        var sut = new GitRepoSyncService(ConfigWithGlobalToken("global-token"), orgs: null);

        var token = await sut.ResolveTokenAsync(orgId: null);

        Assert.Equal("global-token", token);
    }

    [Fact]
    public async Task ResolveTokenAsync_OrgWithConnectedGitHubConnection_UsesConnectionCredentialInstead()
    {
        var orgs = new JsonFileOrgRepository(_tempDir);
        await orgs.UpsertAsync(new Org
        {
            Id = "org1",
            Name = "Acme",
            ContactPerson = "Jane Doe",
            ContactNumber = "555-0100",
            Industry = "Software",
            Connections = [new McpConnection { ServerType = "github", Credential = "mcp-connection-token", EnabledTools = ["list_commits"] }],
        });

        var sut = new GitRepoSyncService(ConfigWithGlobalToken("global-token"), orgs);

        var token = await sut.ResolveTokenAsync("org1");

        Assert.Equal("mcp-connection-token", token);
    }
}
