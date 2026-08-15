using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class KbResearcherAgentTests
{
    [Theory]
    [InlineData("kb_question")]
    [InlineData("code_issue")]
    [InlineData("code_question")]
    public async Task RunAsync_PopulatesKbSnippetsAndSources(string intent)
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "reset password steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/reset.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool, new ListLogger<KbResearcherAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "how do I reset my password", Intent = intent };

        var result = await agent.RunAsync(context);

        Assert.Single(result.KbSnippets);
        Assert.Contains(result.Sources, s => s.Url == "kb/reset.md");
    }

    // U10: a ProductVersion on the context becomes a {"version": ...} metadataFilter passed straight
    // through to IVectorStoreService.QueryAsync -- the existing filter parameter, no new mechanism.
    [Fact]
    public async Task RunAsync_WithProductVersion_PassesVersionAsMetadataFilter()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore
            .Setup(v => v.QueryAsync(
                "proj1-kb", It.IsAny<float[]>(), 5,
                It.Is<IReadOnlyDictionary<string, string>>(f => f != null && f["version"] == "3.0"),
                default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "v3 steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/v3.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool, new ListLogger<KbResearcherAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "how do I install this?", Intent = "kb_question", ProductVersion = "3.0" };

        var result = await agent.RunAsync(context);

        Assert.Single(result.KbSnippets);
        Assert.Equal("v3 steps", result.KbSnippets[0]);
    }

    [Fact]
    public async Task RunAsync_OnRetry_ClearsPreviousSnippetsAndWidensTopK()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 10, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-2", "retry result", 0.2f, new Dictionary<string, string> { ["source"] = "kb/retry.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool, new ListLogger<KbResearcherAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "code_issue" };
        context.KbSnippets.Add("stale snippet from a previous attempt");
        context.KbVerification.Attempts = 1; // simulates: this is a retry

        var result = await agent.RunAsync(context);

        Assert.Single(result.KbSnippets);
        Assert.Equal("retry result", result.KbSnippets[0]);
        Assert.Equal(2, result.KbVerification.Attempts);
    }

    [Theory]
    [InlineData("screenshot_error")]
    [InlineData("unclear")]
    public async Task RunAsync_UnrelatedIntent_DoesNotSearch(string intent)
    {
        var llm = new Mock<ILlmClient>();
        var vectorStore = new Mock<IVectorStoreService>();

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool, new ListLogger<KbResearcherAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = intent };

        var result = await agent.RunAsync(context);

        Assert.Empty(result.KbSnippets);
        Assert.Empty(result.Sources);
        Assert.Equal(0, result.KbVerification.Attempts);
        llm.Verify(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()), Times.Never);
        vectorStore.Verify(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "reset password steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/reset.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var logger = new ListLogger<KbResearcherAgent>();
        var agent = new KbResearcherAgent(tool, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "how do I reset my password", Intent = "kb_question" };

        await agent.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("KbResearcher"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed"));
    }

    [Fact]
    public async Task RunAsync_WhenSearchThrows_LogsFailureAndPropagates()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ThrowsAsync(new InvalidOperationException("embed failed"));

        var vectorStore = new Mock<IVectorStoreService>();
        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var logger = new ListLogger<KbResearcherAgent>();
        var agent = new KbResearcherAgent(tool, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "kb_question" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
