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
}
