using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class KbSearchToolTests
{
    // U17: no negative feedback recorded anywhere -- the default stand-in for every test that
    // isn't specifically exercising down-weighting.
    private static IFeedbackRepository MakeEmptyFeedbackRepository()
    {
        var mock = new Mock<IFeedbackRepository>();
        mock.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<FeedbackEntry>());
        return mock.Object;
    }

    // Mirrors KbVectorIndexerTests.IndexAsync_EmbedsChunks_WithPassagePurpose -- a search query must
    // be embedded as Query, not Passage, for asymmetric embedding models to rank correctly.
    [Fact]
    public async Task SearchAsync_EmbedsQuery_WithQueryPurpose()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult>());
        var tool = new KbSearchTool(llm.Object, vectorStore.Object, MakeEmptyFeedbackRepository());

        await tool.SearchAsync("proj1", "what port does the dashboard use?");

        llm.Verify(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), EmbeddingPurpose.Query), Times.Once);
    }

    // U10: filtered-then-fallback -- a metadataFilter that actually matches something is used as-is,
    // no fallback query is issued.
    [Fact]
    public async Task SearchAsync_WithMetadataFilter_ReturnsFilteredResults_WhenTheyExist()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var filter = new Dictionary<string, string> { ["version"] = "3.0" };
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, filter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "v3 install steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/v3-install.md" }) });
        var tool = new KbSearchTool(llm.Object, vectorStore.Object, MakeEmptyFeedbackRepository());

        var results = await tool.SearchAsync("proj1", "how do I install v3?", 5, filter);

        Assert.Single(results);
        Assert.Equal("v3 install steps", results[0].Text);
        vectorStore.Verify(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    // U10: an untagged corpus (metadataFilter finds nothing) still returns normal results, rather
    // than the customer's version filter silently zeroing out every answer.
    [Fact]
    public async Task SearchAsync_WithMetadataFilter_FallsBackToUnfiltered_WhenFilteredIsEmpty()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var filter = new Dictionary<string, string> { ["version"] = "3.0" };
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, filter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult>());
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "generic install steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/install.md" }) });
        var tool = new KbSearchTool(llm.Object, vectorStore.Object, MakeEmptyFeedbackRepository());

        var results = await tool.SearchAsync("proj1", "how do I install v3?", 5, filter);

        Assert.Single(results);
        Assert.Equal("generic install steps", results[0].Text);
    }

    // U17: a source with repeated negative feedback drops in rank relative to a baseline run with
    // no negative feedback -- the core retrieval down-weighting guarantee this sprint tests for.
    [Fact]
    public async Task SearchAsync_SourceWithRepeatedNegativeFeedback_RanksLowerThanBaseline()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });

        // Two results close in distance -- "penalized.md" starts marginally ahead of "clean.md".
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult>
            {
                new("doc-1", "penalized text", 0.10f, new Dictionary<string, string> { ["source"] = "kb/penalized.md" }),
                new("doc-2", "clean text", 0.12f, new Dictionary<string, string> { ["source"] = "kb/clean.md" }),
            });

        // Baseline: no feedback recorded -- original distance order wins.
        var baselineTool = new KbSearchTool(llm.Object, vectorStore.Object, MakeEmptyFeedbackRepository());
        var baseline = await baselineTool.SearchAsync("proj1", "q");
        Assert.Equal("kb/penalized.md", baseline[0].Source);

        // Same query, but kb/penalized.md has accumulated several "not useful" votes -- its rank
        // must drop below kb/clean.md.
        var feedback = new Mock<IFeedbackRepository>();
        feedback.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<FeedbackEntry>
        {
            new("proj1", "q1", false, false, DateTimeOffset.UtcNow, "u1", new[] { "kb/penalized.md" }, FeedbackReasonCode.Irrelevant),
            new("proj1", "q2", false, false, DateTimeOffset.UtcNow, "u2", new[] { "kb/penalized.md" }, FeedbackReasonCode.Incomplete),
            new("proj1", "q3", false, false, DateTimeOffset.UtcNow, "u3", new[] { "kb/penalized.md" }, FeedbackReasonCode.Other),
        });
        var downweightedTool = new KbSearchTool(llm.Object, vectorStore.Object, feedback.Object);

        var downweighted = await downweightedTool.SearchAsync("proj1", "q");

        Assert.Equal("kb/clean.md", downweighted[0].Source);
        Assert.Equal("kb/penalized.md", downweighted[1].Source);
    }

    // U17: a source with no negative feedback is unaffected.
    [Fact]
    public async Task SearchAsync_SourceWithNoNegativeFeedback_RankUnaffected()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult>
            {
                new("doc-1", "a", 0.10f, new Dictionary<string, string> { ["source"] = "kb/a.md" }),
                new("doc-2", "b", 0.12f, new Dictionary<string, string> { ["source"] = "kb/b.md" }),
            });

        // Feedback exists, but against a different project/source entirely.
        var feedback = new Mock<IFeedbackRepository>();
        feedback.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<FeedbackEntry>
        {
            new("other-proj", "q", false, false, DateTimeOffset.UtcNow, "u1", new[] { "kb/a.md" }, FeedbackReasonCode.Irrelevant),
        });
        var tool = new KbSearchTool(llm.Object, vectorStore.Object, feedback.Object);

        var results = await tool.SearchAsync("proj1", "q");

        Assert.Equal("kb/a.md", results[0].Source);
        Assert.Equal("kb/b.md", results[1].Source);
    }
}
