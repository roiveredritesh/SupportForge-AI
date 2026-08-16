using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class FreshnessCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsStale_WhenAnySourceOlderThan7Days()
    {
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Documents, "docs/", DateTimeOffset.UtcNow.AddDays(-10)),
            },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.False(result.IsFresh);
        Assert.Equal("docs/", result.StaleSources.Single());
    }

    [Fact]
    public void Calculate_ReturnsFresh_WhenAllSourcesWithin7Days()
    {
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs/", DateTimeOffset.UtcNow.AddDays(-1)) },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.True(result.IsFresh);
        Assert.Empty(result.StaleSources);
    }

    [Fact]
    public void Calculate_ReturnsStale_WhenCodeRepoOlderThan7Days()
    {
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig> { new("acme", "widgets", "main", DateTimeOffset.UtcNow.AddDays(-10)) },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.False(result.IsFresh);
        Assert.Equal("acme/widgets", result.StaleSources.Single());
    }

    [Fact]
    public void Calculate_ReturnsStale_WhenCodeRepoNeverSynced()
    {
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig> { new("acme", "widgets", "main", null) },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.False(result.IsFresh);
        Assert.Equal("acme/widgets", result.StaleSources.Single());
    }

    [Fact]
    public void Calculate_Sources_IncludesLastSyncedAtForEverySource()
    {
        var syncedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs/", syncedAt) },
            Repos = new List<GitHubRepoConfig> { new("acme", "widgets", "main", null) },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.Equal(2, result.Sources.Count);
        var docs = result.Sources.Single(s => s.Name == "docs/");
        Assert.Equal(syncedAt, docs.LastSyncedAt);
        Assert.False(docs.IsStale);
        var repo = result.Sources.Single(s => s.Name == "acme/widgets");
        Assert.Null(repo.LastSyncedAt);
        Assert.True(repo.IsStale);
    }
}
