using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class KbResearcherAgentTests
{
    [Fact]
    public async Task RunAsync_PopulatesKbSnippetsAndSources()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "reset password steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/reset.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool);
        var context = new AgentContext { ProjectId = "proj1", Query = "how do I reset my password" };

        var result = await agent.RunAsync(context);

        Assert.Single(result.KbSnippets);
        Assert.Contains(result.Sources, s => s.Url == "kb/reset.md");
    }

    [Fact]
    public async Task RunAsync_OnRetry_ClearsPreviousSnippetsAndWidensTopK()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 10, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-2", "retry result", 0.2f, new Dictionary<string, string> { ["source"] = "kb/retry.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool);
        var context = new AgentContext { ProjectId = "proj1", Query = "q" };
        context.KbSnippets.Add("stale snippet from a previous attempt");
        context.KbVerification.Attempts = 1; // simulates: this is a retry

        var result = await agent.RunAsync(context);

        Assert.Single(result.KbSnippets);
        Assert.Equal("retry result", result.KbSnippets[0]);
        Assert.Equal(2, result.KbVerification.Attempts);
    }
}
