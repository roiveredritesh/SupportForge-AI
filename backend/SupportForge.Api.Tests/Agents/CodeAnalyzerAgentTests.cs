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
        var tool = new Mock<ICodeGraphQueryTool>(MockBehavior.Strict);
        var agent = new CodeAnalyzerAgent(tool.Object, new ListLogger<CodeAnalyzerAgent>());

        var context = new AgentContext { ProjectId = "proj1", Query = "what is my account balance", Intent = "kb_question" };
        var result = await agent.RunAsync(context);

        Assert.Empty(result.CodeSnippets);
        tool.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_SearchesCode_WhenIntentIsCodeQuestion()
    {
        var tool = new Mock<ICodeGraphQueryTool>();
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
        var tool = new Mock<ICodeGraphQueryTool>();
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
        var tool = new Mock<ICodeGraphQueryTool>();
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
        var tool = new Mock<ICodeGraphQueryTool>();
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
        var tool = new Mock<ICodeGraphQueryTool>();
        tool.Setup(t => t.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("query failed"));

        var logger = new ListLogger<CodeAnalyzerAgent>();
        var agent = new CodeAnalyzerAgent(tool.Object, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "code_issue" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void SplitByFile_GivesEachFileItsOwnSnippet_MostRelevantFirst()
    {
        var output = string.Join('\n',
            "Traversal: BFS depth=1 | Start: ['num_pages'] | 3 nodes found",
            "",
            "NODE num_pages [src=django/core/paginator.py loc=L120 community=]",
            "    | def num_pages(self):",
            "NODE Table [src=django/db/backends/ddl_references.py loc=L60 community=]",
            "NODE Paginator [src=django/core/paginator.py loc=L27 community=]",
            "    | class Paginator:",
            "EDGE paginator.py --defines [high]--> Paginator");

        var snippets = CodeAnalyzerAgent.SplitByFile(output);

        Assert.Equal(2, snippets.Count);
        Assert.StartsWith("Traversal: BFS", snippets[0]);
        Assert.Contains("NODE Paginator", snippets[0]);
        Assert.Contains("    | class Paginator:", snippets[0]);
        Assert.Contains("EDGE paginator.py", snippets[0]);
        Assert.Equal("NODE Table [src=django/db/backends/ddl_references.py loc=L60 community=]", snippets[1]);
    }

    [Fact]
    public void SplitByFile_KeepsOutputWithoutNodeLinesWhole()
    {
        Assert.Equal(["plain result"], CodeAnalyzerAgent.SplitByFile("plain result"));
    }
}
