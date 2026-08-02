using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CodeAnalyzerAgentTests
{
    [Fact]
    public async Task RunAsync_SkipsSearch_WhenIntentIsNotCodeIssue()
    {
        var tool = new Mock<IGraphifyQueryTool>(MockBehavior.Strict);
        var agent = new CodeAnalyzerAgent(tool.Object, new ListLogger<CodeAnalyzerAgent>());

        var context = new AgentContext { ProjectId = "proj1", Query = "what is my account balance", Intent = "kb_question" };
        var result = await agent.RunAsync(context);

        Assert.Empty(result.CodeSnippets);
        tool.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_SearchesCode_WhenIntentIsCodeQuestion()
    {
        var tool = new Mock<IGraphifyQueryTool>();
        tool.Setup(t => t.QueryAsync("proj1", "how does CodeAnalyzerAgent work?", false, default))
            .ReturnsAsync("Traversal: BFS depth=2 | Start: [...] | 1 nodes found\n\nNODE CodeAnalyzerAgent [src=CodeAnalyzerAgent.cs loc=L1 community=]");

        var agent = new CodeAnalyzerAgent(tool.Object, new ListLogger<CodeAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "how does CodeAnalyzerAgent work?", Intent = "code_question" };

        var result = await agent.RunAsync(context);

        Assert.Single(result.CodeSnippets);
        Assert.Contains(result.Sources, s => s.Label == "Code: project graph");
    }

    [Fact]
    public async Task RunAsync_WhenToolReturnsNull_ProducesEmptySnippets()
    {
        var tool = new Mock<IGraphifyQueryTool>();
        tool.Setup(t => t.QueryAsync("proj1", "q", false, default)).ReturnsAsync((string?)null);

        var agent = new CodeAnalyzerAgent(tool.Object, new ListLogger<CodeAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "code_issue" };

        var result = await agent.RunAsync(context);

        Assert.Empty(result.CodeSnippets);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task RunAsync_OnRetry_ClearsPreviousSnippetsAndPassesRetryingTrue()
    {
        var tool = new Mock<IGraphifyQueryTool>();
        tool.Setup(t => t.QueryAsync("proj1", "q", true, default)).ReturnsAsync("retry result");

        var agent = new CodeAnalyzerAgent(tool.Object, new ListLogger<CodeAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.Add("stale snippet");
        context.CodeVerification.Attempts = 1;

        var result = await agent.RunAsync(context);

        Assert.Single(result.CodeSnippets);
        Assert.Equal("retry result", result.CodeSnippets[0]);
        Assert.Equal(2, result.CodeVerification.Attempts);
        tool.Verify(t => t.QueryAsync("proj1", "q", true, default), Times.Once);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var tool = new Mock<IGraphifyQueryTool>();
        tool.Setup(t => t.QueryAsync("proj1", "how does CodeAnalyzerAgent work?", false, default))
            .ReturnsAsync("NODE CodeAnalyzerAgent [src=CodeAnalyzerAgent.cs loc=L1]");

        var logger = new ListLogger<CodeAnalyzerAgent>();
        var agent = new CodeAnalyzerAgent(tool.Object, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "how does CodeAnalyzerAgent work?", Intent = "code_question" };

        await agent.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("CodeAnalyzer"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed"));
    }

    [Fact]
    public async Task RunAsync_WhenToolThrows_LogsFailureAndPropagates()
    {
        var tool = new Mock<IGraphifyQueryTool>();
        tool.Setup(t => t.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("graphify failed"));

        var logger = new ListLogger<CodeAnalyzerAgent>();
        var agent = new CodeAnalyzerAgent(tool.Object, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "code_issue" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
