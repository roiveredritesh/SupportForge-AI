using System.Text.Json;
using SupportForge.Api;
using Xunit;

namespace SupportForge.Api.Tests;

public class ProjectOrgMigrationTests
{
    [Fact]
    public async Task RunAsync_BackfillsOrgIdOnOldProjects_AndSeedsDefaultOrgFromLegacyToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        // Simulates pre-existing single-tenant data: no OrgId property at all.
        await File.WriteAllTextAsync(Path.Combine(tempDir, "projects.json"),
            """[{"Id":"proj1","Name":"Legacy Project","Repos":[],"KbSources":[]}]""");

        await ProjectOrgMigration.RunAsync(tempDir, "legacy-pat");

        var projectsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "projects.json"));
        using var projectsDoc = JsonDocument.Parse(projectsJson);
        Assert.Equal("default", projectsDoc.RootElement[0].GetProperty("OrgId").GetString());

        var orgsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "orgs.json"));
        using var orgsDoc = JsonDocument.Parse(orgsJson);
        Assert.Equal("default", orgsDoc.RootElement[0].GetProperty("Id").GetString());
        Assert.Equal("legacy-pat", orgsDoc.RootElement[0].GetProperty("GitHubAccessToken").GetString());

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunAsync_NoOp_WhenAllProjectsAlreadyHaveOrgId()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "projects.json"),
            """[{"Id":"proj1","Name":"Already Migrated","OrgId":"acme","Repos":[],"KbSources":[]}]""");

        await ProjectOrgMigration.RunAsync(tempDir, "legacy-pat");

        Assert.False(File.Exists(Path.Combine(tempDir, "orgs.json")));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunAsync_NoOp_WhenProjectsFileDoesNotExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        await ProjectOrgMigration.RunAsync(tempDir, "legacy-pat");

        Assert.False(File.Exists(Path.Combine(tempDir, "orgs.json")));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunAsync_DoesNotOverwriteExistingOrgsFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "projects.json"),
            """[{"Id":"proj1","Name":"Legacy Project","Repos":[],"KbSources":[]}]""");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "orgs.json"),
            """[{"Id":"acme","Name":"Acme","GitHubAccessToken":"already-set"}]""");

        await ProjectOrgMigration.RunAsync(tempDir, "legacy-pat");

        var orgsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "orgs.json"));
        using var orgsDoc = JsonDocument.Parse(orgsJson);
        Assert.Single(orgsDoc.RootElement.EnumerateArray());
        Assert.Equal("acme", orgsDoc.RootElement[0].GetProperty("Id").GetString());

        var projectsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "projects.json"));
        using var projectsDoc = JsonDocument.Parse(projectsJson);
        Assert.Equal("default", projectsDoc.RootElement[0].GetProperty("OrgId").GetString());

        Directory.Delete(tempDir, recursive: true);
    }
}
