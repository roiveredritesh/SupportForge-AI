using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class TriageAgentTests
{
    [Fact]
    public async Task RunAsync_SetsIntent_FromLlmResponse()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("code_issue");

        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error on checkout" };

        var result = await agent.RunAsync(context);

        Assert.Equal("code_issue", result.Intent);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("code_issue");

        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error on checkout" };

        await agent.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("Triage"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed") && e.Message.Contains("code_issue"));
    }

    [Fact]
    public async Task RunAsync_WhenLlmThrows_LogsFailureAndPropagates()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ThrowsAsync(new InvalidOperationException("llm down"));

        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "it broke" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task RunAsync_SetsUnclearIntent_WithoutSpecialCasing()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("unclear\n");

        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "it broke" };

        var result = await agent.RunAsync(context);

        Assert.Equal("unclear", result.Intent);
    }

    [Theory]
    [InlineData("\"kb_question\"", "kb_question")]
    [InlineData("`code_issue`", "code_issue")]
    [InlineData("**code_question**", "code_question")]
    [InlineData("code_issue.", "code_issue")]
    [InlineData("  Screenshot_Error \n", "screenshot_error")]
    [InlineData("bug_report", "unclear")]
    [InlineData("This looks like a bug in the checkout flow.", "unclear")]
    [InlineData("", "unclear")]
    public async Task RunAsync_NormalizesAndValidatesLabel(string llmResponse, string expected)
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync(llmResponse);

        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "something happened" };

        var result = await agent.RunAsync(context);

        Assert.Equal(expected, result.Intent);
    }

    [Fact]
    public async Task RunAsync_SystemPrompt_OffersUnclearLabel()
    {
        string? systemPrompt = null;
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .Callback((string s, string _, CancellationToken _) => systemPrompt = s)
           .ReturnsAsync("unclear");

        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());
        await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = "it broke" });

        Assert.Contains("\"unclear\"", systemPrompt);
    }
}
