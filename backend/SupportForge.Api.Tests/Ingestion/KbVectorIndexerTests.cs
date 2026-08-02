using Moq;
using SupportForge.Agents;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class KbVectorIndexerTests
{
    [Fact]
    public async Task IndexAsync_ChunksEachDocument_EmbedsEachChunk_ThenUpsertsOnce()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new float[] { 0.1f, 0.2f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object);

        // 1500 chars > DocumentChunker's default 1000-char budget, so this file alone produces 2+ chunks.
        var longText = string.Join(' ', Enumerable.Repeat("word", 400));
        await indexer.IndexAsync("proj1", [("file-a.md", longText), ("file-b.md", "short")], CancellationToken.None);

        Assert.NotNull(upserted);
        var expectedChunks = SupportForge.Ingestion.Documents.DocumentChunker.Chunk(longText).Count + 1; // + file-b's single chunk
        Assert.Equal(expectedChunks, upserted!.Count);
        Assert.All(upserted, d => Assert.Equal(2, d.Embedding.Length));
        Assert.Contains(upserted, d => d.Metadata["source"] == "file-a.md" && d.Metadata["chunk"] == "0");
        Assert.Contains(upserted, d => d.Metadata["source"] == "file-b.md" && d.Text == "short");
        vectorStore.Verify(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IndexAsync_NoDocuments_DoesNotCallUpsert()
    {
        var llm = new Mock<ILlmEmbeddingClient>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStoreService>(MockBehavior.Strict);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object);

        await indexer.IndexAsync("proj1", [], CancellationToken.None);

        vectorStore.VerifyNoOtherCalls();
        llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IndexAsync_GeneratesDistinctIdsPerChunk()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object);

        await indexer.IndexAsync("proj1", [("a.md", "one"), ("b.md", "two")], CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.Equal(upserted!.Select(d => d.Id).Distinct().Count(), upserted.Count);
    }
}
