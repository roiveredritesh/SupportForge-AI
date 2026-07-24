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
}
