using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class ProjectOrgMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileProjectRepository _projects;
    private readonly JsonFileOrgRepository _orgs;

    public ProjectOrgMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _projects = new JsonFileProjectRepository(_tempDir);
        _orgs = new JsonFileOrgRepository(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FreshStore_NoProjects_CreatesNoDefaultOrg()
    {
        await ProjectOrgMigration.RunAsync(_projects, _orgs);

        Assert.Empty(await _orgs.GetAllAsync());
    }

    [Fact]
    public async Task ProjectsMissingOrgId_BackfilledToOneNewDefaultOrg()
    {
        await _projects.UpsertAsync(new Project { Id = "p1", Name = "One" });
        await _projects.UpsertAsync(new Project { Id = "p2", Name = "Two" });
        await _projects.UpsertAsync(new Project { Id = "p3", Name = "Three" });

        await ProjectOrgMigration.RunAsync(_projects, _orgs);

        var orgs = await _orgs.GetAllAsync();
        Assert.Single(orgs);

        var all = await _projects.GetAllAsync();
        Assert.All(all, p => Assert.Equal(orgs[0].Id, p.OrgId));
    }

    [Fact]
    public async Task RunTwice_IsIdempotent_NoDuplicateOrgNoDoubleBackfill()
    {
        await _projects.UpsertAsync(new Project { Id = "p1", Name = "One" });
        await _projects.UpsertAsync(new Project { Id = "p2", Name = "Two" });

        await ProjectOrgMigration.RunAsync(_projects, _orgs);
        await ProjectOrgMigration.RunAsync(_projects, _orgs);

        var orgs = await _orgs.GetAllAsync();
        Assert.Single(orgs);

        var all = await _projects.GetAllAsync();
        Assert.All(all, p => Assert.Equal(orgs[0].Id, p.OrgId));
    }

    [Fact]
    public async Task ProjectWithOrgIdAlreadySet_IsLeftUntouched()
    {
        await _projects.UpsertAsync(new Project { Id = "p1", Name = "Already Assigned", OrgId = "some-other-org" });
        await _projects.UpsertAsync(new Project { Id = "p2", Name = "Needs Backfill" });

        await ProjectOrgMigration.RunAsync(_projects, _orgs);

        var p1 = await _projects.GetByIdAsync("p1");
        var p2 = await _projects.GetByIdAsync("p2");

        Assert.Equal("some-other-org", p1!.OrgId);
        Assert.Equal(ProjectOrgMigration.DefaultOrgId, p2!.OrgId);

        // "some-other-org" was never created by the migration (only referenced by the pre-assigned project).
        Assert.Null(await _orgs.GetByIdAsync("some-other-org"));
    }
}
