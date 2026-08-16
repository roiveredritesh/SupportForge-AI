using System.Text.Json;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class OrgRequiredFieldsMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public OrgRequiredFieldsMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string OrgsPath => Path.Combine(_tempDir, "orgs.json");

    [Fact]
    public async Task NoOrgsFile_NoOp()
    {
        await OrgRequiredFieldsMigration.RunAsync(_tempDir);

        Assert.False(File.Exists(OrgsPath));
    }

    [Fact]
    public async Task LegacyOrgMissingRequiredFields_BackfillsThemAndStaysReadableByStrictOrgModel()
    {
        // Simulates an org written before ContactPerson/ContactNumber/Industry existed --
        // System.Text.Json would throw deserializing this straight into the strict Org model.
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new { Id = "legacy-org", Name = "Legacy Org", CreatedAt = DateTimeOffset.UtcNow, Connections = new object[0] },
        });
        await File.WriteAllTextAsync(OrgsPath, legacyJson);

        await OrgRequiredFieldsMigration.RunAsync(_tempDir);

        // The whole point: JsonFileOrgRepository's strict Org deserialization must now succeed.
        var repo = new JsonFileOrgRepository(_tempDir);
        var orgs = await repo.GetAllAsync();
        var org = Assert.Single(orgs);
        Assert.Equal("legacy-org", org.Id);
        Assert.Equal("Unknown", org.ContactPerson);
        Assert.Equal("Unknown", org.ContactNumber);
        Assert.Equal("Unknown", org.Industry);
    }

    [Fact]
    public async Task OrgAlreadyHasRequiredFields_LeftUnchanged()
    {
        var repo = new JsonFileOrgRepository(_tempDir);
        await repo.UpsertAsync(new Org
        {
            Id = "modern-org",
            Name = "Modern Org",
            ContactPerson = "Jane Doe",
            ContactNumber = "+1-555-0100",
            Industry = "Healthcare",
        });

        await OrgRequiredFieldsMigration.RunAsync(_tempDir);

        var org = Assert.Single(await repo.GetAllAsync());
        Assert.Equal("Jane Doe", org.ContactPerson);
        Assert.Equal("+1-555-0100", org.ContactNumber);
        Assert.Equal("Healthcare", org.Industry);
    }

    [Fact]
    public async Task MixOfLegacyAndModernOrgs_OnlyLegacyOnesBackfilled()
    {
        var legacyJson = JsonSerializer.Serialize(new object[]
        {
            new { Id = "legacy-org", Name = "Legacy Org", CreatedAt = DateTimeOffset.UtcNow, Connections = new object[0] },
            new { Id = "modern-org", Name = "Modern Org", ContactPerson = "Jane Doe", ContactNumber = "+1-555-0100", Industry = "Healthcare", CreatedAt = DateTimeOffset.UtcNow, Connections = new object[0] },
        });
        await File.WriteAllTextAsync(OrgsPath, legacyJson);

        await OrgRequiredFieldsMigration.RunAsync(_tempDir);

        var repo = new JsonFileOrgRepository(_tempDir);
        var orgs = (await repo.GetAllAsync()).ToDictionary(o => o.Id);
        Assert.Equal("Unknown", orgs["legacy-org"].ContactPerson);
        Assert.Equal("Jane Doe", orgs["modern-org"].ContactPerson);
    }

    [Fact]
    public async Task RunTwice_Idempotent()
    {
        var legacyJson = JsonSerializer.Serialize(new[]
        {
            new { Id = "legacy-org", Name = "Legacy Org", CreatedAt = DateTimeOffset.UtcNow, Connections = new object[0] },
        });
        await File.WriteAllTextAsync(OrgsPath, legacyJson);

        await OrgRequiredFieldsMigration.RunAsync(_tempDir);
        await OrgRequiredFieldsMigration.RunAsync(_tempDir);

        var repo = new JsonFileOrgRepository(_tempDir);
        var org = Assert.Single(await repo.GetAllAsync());
        Assert.Equal("Unknown", org.ContactPerson);
    }
}
