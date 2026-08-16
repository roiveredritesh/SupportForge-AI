using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using SupportForge.Api.Tests.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
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

    private static ITokenUsageRepository NoOpTokenUsage() => new Mock<ITokenUsageRepository>().Object;

    private static NullLogger<KbVectorIndexer> NoOpLogger() => NullLogger<KbVectorIndexer>.Instance;

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object, NoOpTokenUsage(), NoOpLogger());

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
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object, NoOpTokenUsage(), NoOpLogger());

        await indexer.IndexAsync("proj1", [("file.md", "new content", null)], CancellationToken.None);

        llm.Verify(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()), Times.Once);
        vectorStore.Verify(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()), Times.Once);
        hashes.Verify(h => h.SetHashAsync("proj1", "file.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IndexAsync_SumsEmbeddingTokensAcrossChunks_AndRecordsThemAsIngestionUsage()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        llm.SetupSequence(l => l.LastTotalTokens).Returns(10).Returns(15);
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var tokenUsage = new Mock<ITokenUsageRepository>();
        TokenUsageEntry? recorded = null;
        tokenUsage.Setup(t => t.AddAsync(It.IsAny<TokenUsageEntry>(), It.IsAny<CancellationToken>()))
            .Callback<TokenUsageEntry, CancellationToken>((e, _) => recorded = e)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), tokenUsage.Object, NoOpLogger());

        await indexer.IndexAsync("proj1", [("a.md", "one", null), ("b.md", "two", null)], CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal("proj1", recorded!.ProjectId);
        Assert.Equal(25, recorded.TotalTokens);
        Assert.Equal("ingestion", recorded.Source);
        // Characterization baseline (U7): no triggeredByUserId argument -- today's default,
        // unattributed behavior, unchanged by U7's optional parameter.
        Assert.Null(recorded.UserId);
    }

    // U7: an interactive Trigger/ForceReindex run threads the calling Admin's id all the way
    // through to this TokenUsageEntry write.
    [Fact]
    public async Task IndexAsync_WithTriggeredByUserId_StampsUserIdOnTokenUsageEntry()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        llm.Setup(l => l.LastTotalTokens).Returns(10);
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var tokenUsage = new Mock<ITokenUsageRepository>();
        TokenUsageEntry? recorded = null;
        tokenUsage.Setup(t => t.AddAsync(It.IsAny<TokenUsageEntry>(), It.IsAny<CancellationToken>()))
            .Callback<TokenUsageEntry, CancellationToken>((e, _) => recorded = e)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), tokenUsage.Object, NoOpLogger());

        await indexer.IndexAsync("proj1", [("a.md", "one", null)], CancellationToken.None, triggeredByUserId: "admin1");

        Assert.NotNull(recorded);
        Assert.Equal("admin1", recorded!.UserId);
    }

    [Fact]
    public async Task IndexAsync_NoDocuments_DoesNotRecordTokenUsage()
    {
        var llm = new Mock<ILlmEmbeddingClient>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStoreService>(MockBehavior.Strict);
        var tokenUsage = new Mock<ITokenUsageRepository>(MockBehavior.Strict);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), tokenUsage.Object, NoOpLogger());

        await indexer.IndexAsync("proj1", [], CancellationToken.None);

        tokenUsage.VerifyNoOtherCalls();
    }

    // U6: a single chunk's embedding failure is logged and skipped, not fatal to the whole indexing run.
    [Fact]
    public async Task IndexAsync_MiddleChunkEmbedFails_StillUpsertsOtherChunks_AndDoesNotThrow()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        // Calls happen in per-chunk order, so the 2nd call corresponds to the 2nd chunk (index 1).
        llm.SetupSequence(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f })
            .ThrowsAsync(new InvalidOperationException("embedding service unavailable"))
            .ReturnsAsync(new float[] { 0.1f })
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

        var longText = string.Join(' ', Enumerable.Repeat("word", 700));
        var chunks = DocumentChunker.Chunk(longText);
        Assert.True(chunks.Count >= 3, "test needs a document that chunks into at least 3 pieces");

        await indexer.IndexAsync("proj1", [("file-a.md", longText, null)], CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.Equal(chunks.Count - 1, upserted!.Count);
        Assert.DoesNotContain(upserted, d => d.Metadata["chunk"] == "1");
        Assert.Contains(upserted, d => d.Metadata["chunk"] == "0");
    }

    // U6: a document that has at least one successfully-embedded chunk still gets its hash set, even
    // if another chunk in the same document failed.
    [Fact]
    public async Task IndexAsync_MiddleChunkEmbedFails_StillSetsHash_ForPartiallySuccessfulDocument()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.SetupSequence(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f })
            .ThrowsAsync(new InvalidOperationException("boom"))
            .ReturnsAsync(new float[] { 0.1f })
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object, NoOpTokenUsage(), NoOpLogger());

        var longText = string.Join(' ', Enumerable.Repeat("word", 700));
        await indexer.IndexAsync("proj1", [("file-a.md", longText, null)], CancellationToken.None);

        hashes.Verify(h => h.SetHashAsync("proj1", "file-a.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // U6: zero successfully-embedded chunks means the document's hash is not updated, so it retries
    // on the next sync -- but a sibling document that fully succeeds still gets its hash set and upserted.
    [Fact]
    public async Task IndexAsync_DocumentWithAllChunksFailing_SkipsSetHashAsync_ButSiblingDocumentStillSucceeds()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync("fail content", It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        llm.Setup(l => l.EmbedAsync("ok content", It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object, NoOpTokenUsage(), NoOpLogger());

        await indexer.IndexAsync(
            "proj1", [("fail.md", "fail content", null), ("ok.md", "ok content", null)], CancellationToken.None);

        hashes.Verify(h => h.SetHashAsync("proj1", "fail.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        hashes.Verify(h => h.SetHashAsync("proj1", "ok.md", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(upserted);
        Assert.Single(upserted!);
        Assert.Equal("ok.md", upserted![0].Metadata["source"]);
    }

    // U6: cancellation is not a skippable per-chunk failure -- it must still propagate out of IndexAsync.
    [Fact]
    public async Task IndexAsync_OperationCanceledDuringEmbed_PropagatesOutOfIndexAsync()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ThrowsAsync(new OperationCanceledException());
        var vectorStore = new Mock<IVectorStoreService>(MockBehavior.Strict);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            indexer.IndexAsync("proj1", [("file.md", "content", null)], CancellationToken.None));
    }

    // U6: one document entirely failing doesn't prevent other documents in the same call from being indexed.
    [Fact]
    public async Task IndexAsync_OneDocumentFullyFailing_DoesNotBlockOtherDocumentsInSameCall()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync("fail content", It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        llm.Setup(l => l.EmbedAsync("ok content", It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

        var exception = await Record.ExceptionAsync(() => indexer.IndexAsync(
            "proj1", [("fail.md", "fail content", null), ("ok.md", "ok content", null)], CancellationToken.None));

        Assert.Null(exception);
        Assert.NotNull(upserted);
        Assert.Contains(upserted, d => d.Metadata["source"] == "ok.md");
    }

    // U6: the warning logged on a caught per-chunk embedding failure carries the source ref and chunk index.
    [Fact]
    public async Task IndexAsync_ChunkEmbedFails_LogsWarning_WithSourceRefAndChunkIndex()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ThrowsAsync(new InvalidOperationException("embedding service unavailable"));
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var logger = new ListLogger<KbVectorIndexer>();
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), logger);

        await indexer.IndexAsync("proj1", [("file.md", "some content", null)], CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("file.md", warning.Message);
        Assert.Contains("0", warning.Message);
        Assert.NotNull(warning.Exception);
    }

    // Bug fix: a source removed between syncs (e.g. a deleted file) previously left its old hash
    // entry and vector chunks behind forever. IndexAsync's optional PruneScope closes that gap.
    [Fact]
    public async Task IndexAsync_WithPruneScope_DeletesHashAndVectorChunks_ForRefsNoLongerPresent()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        IReadOnlyDictionary<string, string>? deletedFilter = null;
        vectorStore.Setup(v => v.DeleteByMetadataAsync("proj1-kb", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, string>, CancellationToken>((_, filter, _) => deletedFilter = filter)
            .Returns(Task.CompletedTask);
        var contentHashes = new Mock<IContentHashRepository>();
        contentHashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        contentHashes.Setup(h => h.GetSourceRefsAsync("proj1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "/docs/still-here.md", "/docs/deleted.md" });
        string? deletedRef = null;
        contentHashes.Setup(h => h.DeleteAsync("proj1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, r, _) => deletedRef = r)
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, contentHashes.Object, NoOpTokenUsage(), NoOpLogger());

        await indexer.IndexAsync(
            "proj1", [("/docs/still-here.md", "content", null)], CancellationToken.None,
            prune: new PruneScope("/docs/", new HashSet<string> { "/docs/still-here.md" }));

        Assert.Equal("/docs/deleted.md", deletedRef);
        Assert.NotNull(deletedFilter);
        Assert.Equal("/docs/deleted.md", deletedFilter!["source"]);
        contentHashes.Verify(h => h.DeleteAsync("proj1", "/docs/still-here.md", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IndexAsync_WithPruneScope_DoesNotPruneRefsOutsideItsOwnPrefix()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var contentHashes = new Mock<IContentHashRepository>();
        contentHashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        // "/other-source/page.md" belongs to a different KB source on the same project -- pruning
        // "/docs/" must not touch it even though it's absent from this run's current refs.
        contentHashes.Setup(h => h.GetSourceRefsAsync("proj1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "/docs/still-here.md", "/other-source/page.md" });
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, contentHashes.Object, NoOpTokenUsage(), NoOpLogger());

        await indexer.IndexAsync(
            "proj1", [("/docs/still-here.md", "content", null)], CancellationToken.None,
            prune: new PruneScope("/docs/", new HashSet<string> { "/docs/still-here.md" }));

        contentHashes.Verify(h => h.DeleteAsync("proj1", "/other-source/page.md", It.IsAny<CancellationToken>()), Times.Never);
        vectorStore.Verify(v => v.DeleteByMetadataAsync(
            It.IsAny<string>(), It.Is<IReadOnlyDictionary<string, string>>(f => f["source"] == "/other-source/page.md"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IndexAsync_WithoutPruneScope_NeverCallsPruneRelatedMethods()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>()))
            .ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, AlwaysUnseenHashes(), NoOpTokenUsage(), NoOpLogger());

        await indexer.IndexAsync("proj1", [("/docs/a.md", "content", null)], CancellationToken.None);

        vectorStore.Verify(v => v.DeleteByMetadataAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
