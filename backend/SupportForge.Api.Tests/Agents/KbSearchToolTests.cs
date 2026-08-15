using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class KbSearchToolTests
{
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
        var tool = new KbSearchTool(llm.Object, vectorStore.Object);

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
        var tool = new KbSearchTool(llm.Object, vectorStore.Object);

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
        var tool = new KbSearchTool(llm.Object, vectorStore.Object);

        var results = await tool.SearchAsync("proj1", "how do I install v3?", 5, filter);

        Assert.Single(results);
        Assert.Equal("generic install steps", results[0].Text);
    }
}
