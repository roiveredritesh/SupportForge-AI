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

        var agent = new TriageAgent(llm.Object);
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error on checkout" };

        var result = await agent.RunAsync(context);

        Assert.Equal("code_issue", result.Intent);
    }

    [Fact]
    public async Task RunAsync_SetsUnclearIntent_WithoutSpecialCasing()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("unclear\n");

        var agent = new TriageAgent(llm.Object);
        var context = new AgentContext { ProjectId = "proj1", Query = "it broke" };

        var result = await agent.RunAsync(context);

        Assert.Equal("unclear", result.Intent);
    }

    [Fact]
    public async Task RunAsync_SystemPrompt_OffersUnclearLabel()
    {
        string? systemPrompt = null;
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .Callback((string s, string _, CancellationToken _) => systemPrompt = s)
           .ReturnsAsync("unclear");

        var agent = new TriageAgent(llm.Object);
        await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = "it broke" });

        Assert.Contains("\"unclear\"", systemPrompt);
    }
}
