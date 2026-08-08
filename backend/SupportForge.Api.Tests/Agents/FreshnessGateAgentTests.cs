using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class FreshnessGateAgentTests
{
    private static Project FreshProject() => new()
    {
        Id = "p",
        Name = "p",
        OrgId = "test-org",
        KbSources = [new KbSourceConfig(KbSourceType.Documents, "docs/", DateTimeOffset.UtcNow)],
        Repos = [],
    };

    [Fact]
    public async Task RunAsync_SyncInProgress_SetsSyncInProgressTrue()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync("p", It.IsAny<CancellationToken>())).ReturnsAsync(FreshProject());
        var activity = new Mock<IIngestionActivity>();
        activity.Setup(a => a.IsBusy("p")).Returns(true);
        var agent = new FreshnessGateAgent(projects.Object, activity.Object, NullLogger<FreshnessGateAgent>.Instance);
        var context = new AgentContext { ProjectId = "p", Query = "q" };

        var result = await agent.RunAsync(context);

        Assert.NotNull(result.Freshness);
        Assert.True(result.Freshness!.SyncInProgress);
    }

    [Fact]
    public async Task RunAsync_NoSyncInProgress_FreshProject_ReportsFresh()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync("p", It.IsAny<CancellationToken>())).ReturnsAsync(FreshProject());
        var activity = new Mock<IIngestionActivity>();
        activity.Setup(a => a.IsBusy("p")).Returns(false);
        var agent = new FreshnessGateAgent(projects.Object, activity.Object, NullLogger<FreshnessGateAgent>.Instance);
        var context = new AgentContext { ProjectId = "p", Query = "q" };

        var result = await agent.RunAsync(context);

        Assert.False(result.Freshness!.SyncInProgress);
        Assert.True(result.Freshness.Score.IsFresh);
    }

    [Fact]
    public async Task RunAsync_ProjectNotFound_DoesNotThrow_ReportsFresh()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);
        var activity = new Mock<IIngestionActivity>();
        activity.Setup(a => a.IsBusy("missing")).Returns(false);
        var agent = new FreshnessGateAgent(projects.Object, activity.Object, NullLogger<FreshnessGateAgent>.Instance);
        var context = new AgentContext { ProjectId = "missing", Query = "q" };

        var result = await agent.RunAsync(context);

        Assert.True(result.Freshness!.Score.IsFresh);
    }
}
