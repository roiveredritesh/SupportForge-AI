using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class KbVectorIndexerTests
{
    // Always reports "no prior hash" -- every document looks unchanged-since-never, so existing
    // tests' pre-D1 assumption ("every document gets chunked/embedded") still holds.
    private static IContentHashRepository AlwaysUnseenHashes()
    {
        var mock = new Mock<IContentHashRepository>();
        mock.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        return mock.Object;
    }

    [Fact]
    public async Task IndexAsync_ChunksEachDocument_EmbedsEachChunk_ThenUpsertsOnce()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f, 0.2f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes());

        // 1500 chars > DocumentChunker's default 1000-char budget, so this file alone produces 2+ chunks.
        var longText = string.Join(' ', Enumerable.Repeat("word", 400));
        await indexer.IndexAsync("proj1", [("file-a.md", longText, null), ("file-b.md", "short", null)], CancellationToken.None);

        Assert.NotNull(upserted);
        var expectedChunks = DocumentChunker.Chunk(longText).Count + 1; // + file-b's single chunk
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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes());

        await indexer.IndexAsync("proj1", [], CancellationToken.None);

        vectorStore.VerifyNoOtherCalls();
        llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IndexAsync_GeneratesDistinctIdsPerChunk()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes());

        await indexer.IndexAsync("proj1", [("a.md", "one", null), ("b.md", "two", null)], CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.Equal(upserted!.Select(d => d.Id).Distinct().Count(), upserted.Count);
    }

    // Asymmetric embedding models (e.g. NIM's nv-embedqa-e5-v5) rank poorly if documents are embedded
    // as queries -- indexing must request Passage, not the default Query.
    [Fact]
    public async Task IndexAsync_EmbedsChunks_WithPassagePurpose()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes());

        await indexer.IndexAsync("proj1", [("a.md", "one", null)], CancellationToken.None);

        llm.Verify(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), EmbeddingPurpose.Passage), Times.Once);
    }

    // D1 (gap-closing-solutions.md Phase D, item 1): non-null Title becomes chunk metadata.
    [Fact]
    public async Task IndexAsync_WithTitle_AttachesItAsMetadata()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes());

        await indexer.IndexAsync("proj1", [("page-1", "content", "How to Configure Widgets")], CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.All(upserted, d => Assert.Equal("How to Configure Widgets", d.Metadata["title"]));
    }

    [Fact]
    public async Task IndexAsync_WithoutTitle_OmitsTitleMetadataKey()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes());

        await indexer.IndexAsync("proj1", [("file.md", "content", null)], CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.All(upserted, d => Assert.False(d.Metadata.ContainsKey("title")));
    }

    // D1: unchanged content since the last successful index is skipped entirely.
    [Fact]
    public async Task IndexAsync_UnchangedContent_SkipsReChunkAndReEmbed_AndDoesNotUpsert()
    {
        var llm = new Mock<ILlmEmbeddingClient>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStoreService>(MockBehavior.Strict);
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync("proj1", "file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("same content"))));
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object);

        await indexer.IndexAsync("proj1", [("file.md", "same content", null)], CancellationToken.None);

        llm.VerifyNoOtherCalls();
        vectorStore.VerifyNoOtherCalls();
        hashes.Verify(h => h.SetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IndexAsync_ChangedContent_ReIndexes_AndUpdatesStoredHash()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync("proj1", "file.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync("stale-hash-from-a-previous-version-of-the-file");
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object);

        await indexer.IndexAsync("proj1", [("file.md", "new content", null)], CancellationToken.None);

        llm.Verify(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()), Times.Once);
        vectorStore.Verify(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()), Times.Once);
        hashes.Verify(h => h.SetHashAsync("proj1", "file.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
