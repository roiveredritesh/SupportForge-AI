using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class TokenUsageTests
{
    [Fact]
    public async Task AddAsync_Then_GetTotalForProjectAsync_SumsTokensForThatProjectOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileTokenUsageRepository(tempDir);

        await repo.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow, "chat"));
        await repo.AddAsync(new TokenUsageEntry("proj1", 50, DateTimeOffset.UtcNow, "chat"));
        await repo.AddAsync(new TokenUsageEntry("proj2", 999, DateTimeOffset.UtcNow, "chat"));

        var total = await repo.GetTotalForProjectAsync("proj1");

        Assert.Equal(150, total);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetTotalsBySourceForProjectAsync_GroupsTokensBySource_ForThatProjectOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileTokenUsageRepository(tempDir);

        await repo.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow, "chat"));
        await repo.AddAsync(new TokenUsageEntry("proj1", 40, DateTimeOffset.UtcNow, "ingestion"));
        await repo.AddAsync(new TokenUsageEntry("proj1", 10, DateTimeOffset.UtcNow, "ingestion"));
        await repo.AddAsync(new TokenUsageEntry("proj2", 999, DateTimeOffset.UtcNow, "chat"));

        var bySource = await repo.GetTotalsBySourceForProjectAsync("proj1");

        Assert.Equal(100, bySource["chat"]);
        Assert.Equal(50, bySource["ingestion"]);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetEntriesForProjectAsync_ReturnsOnlyThatProjectsEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileTokenUsageRepository(tempDir);

        await repo.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow, "chat"));
        await repo.AddAsync(new TokenUsageEntry("proj1", 40, DateTimeOffset.UtcNow, "ingestion"));
        await repo.AddAsync(new TokenUsageEntry("proj2", 999, DateTimeOffset.UtcNow, "chat"));

        var entries = await repo.GetEntriesForProjectAsync("proj1");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("proj1", e.ProjectId));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetEntriesForProjectsAsync_ReturnsEntriesFromMultipleProjects_ExcludesOthers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileTokenUsageRepository(tempDir);

        await repo.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow, "chat"));
        await repo.AddAsync(new TokenUsageEntry("proj2", 50, DateTimeOffset.UtcNow, "chat"));
        await repo.AddAsync(new TokenUsageEntry("proj3", 999, DateTimeOffset.UtcNow, "chat"));

        var entries = await repo.GetEntriesForProjectsAsync(new[] { "proj1", "proj2" });

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ProjectId == "proj1");
        Assert.Contains(entries, e => e.ProjectId == "proj2");
        Assert.DoesNotContain(entries, e => e.ProjectId == "proj3");
        Directory.Delete(tempDir, recursive: true);
    }
}
