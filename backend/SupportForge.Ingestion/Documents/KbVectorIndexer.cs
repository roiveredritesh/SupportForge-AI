using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Documents;

// Bug fix: a file/page removed from a source between syncs previously left its old hash entry and
// vector-store chunks behind forever (IndexAsync only ever adds/updates, never deletes), so a
// deleted file kept showing up in chat answers even after Force Reindex. A caller that can name a
// stable ref-prefix its source owns (Documents/GitHubFolder: the folder path every sourceRef is
// rooted under) and enumerate every ref that currently exists (regardless of whether extraction
// succeeded this run) can opt into pruning via this scope. Website/Confluence don't pass one:
// Website's crawl is non-exhaustive by design (a page unreachable this run isn't necessarily
// deleted) and Confluence is a single page per source with no "still exists" listing to check
// against, so neither can safely tell "removed" apart from "just not seen this run".
public sealed record PruneScope(string RefPrefix, IReadOnlySet<string> CurrentSourceRefs);

/// <summary>
/// Shared chunk-embed-upsert path for every KB source type (Documents, GitHub-folder, Confluence,
/// Website): each source's raw text is chunked, embedded, and upserted into the project's
/// "{projectId}-kb" Chroma collection.
/// </summary>
public sealed class KbVectorIndexer
{
    private readonly ILlmEmbeddingClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly IContentHashRepository _contentHashes;
    private readonly ITokenUsageRepository _tokenUsage;
    private readonly ILogger<KbVectorIndexer> _logger;

    public KbVectorIndexer(
        ILlmEmbeddingClient llm, IVectorStoreService vectorStore, IContentHashRepository contentHashes,
        ITokenUsageRepository tokenUsage, ILogger<KbVectorIndexer> logger)
    {
        _llm = llm;
        _vectorStore = vectorStore;
        _contentHashes = contentHashes;
        _tokenUsage = tokenUsage;
        _logger = logger;
    }

    // D1 (gap-closing-solutions.md Phase D, item 1): Title is optional richer metadata (Confluence
    // page title, Website <title>) attached per chunk when the source format provides one -- null
    // for sources with no natural title distinct from their SourceRef (Documents file paths).
    public async Task IndexAsync(
        string projectId, IEnumerable<(string SourceRef, string Text, string? Title)> documents,
        CancellationToken ct, string? triggeredByUserId = null, PruneScope? prune = null)
    {
        var vectorDocs = new List<VectorDocument>();
        var tokensUsed = 0;
        foreach (var (sourceRef, text, title) in documents)
        {
            // D1: unchanged content since the last successful index is skipped entirely -- no
            // re-chunk, no re-embed, no upsert call. A document that changes even slightly still
            // gets fully re-processed; this only saves work for genuinely untouched sources.
            var hash = ComputeHash(text);
            var previousHash = await _contentHashes.GetHashAsync(projectId, sourceRef, ct);
            if (previousHash == hash) continue;

            var chunks = DocumentChunker.Chunk(text);
            var anyChunkEmbedded = false;
            for (var i = 0; i < chunks.Count; i++)
            {
                float[] embedding;
                try
                {
                    embedding = await _llm.EmbedAsync(chunks[i], ct, EmbeddingPurpose.Passage);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // U6: a single chunk's embedding failure is logged and skipped, not fatal to the
                    // whole indexing run -- other chunks/documents in this batch still get indexed.
                    _logger.LogWarning(ex, "Failed to embed chunk {ChunkIndex} of {SourceRef}; skipping chunk", i, sourceRef);
                    continue;
                }

                anyChunkEmbedded = true;
                tokensUsed += _llm.LastTotalTokens;
                var metadata = new Dictionary<string, string> { ["source"] = sourceRef, ["chunk"] = i.ToString() };
                if (!string.IsNullOrWhiteSpace(title)) metadata["title"] = title;
                vectorDocs.Add(new VectorDocument(
                    Id: $"{SanitizeId(sourceRef)}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: metadata));
            }

            // U6: don't mark a document as synced if zero chunks succeeded -- a transient failure
            // should retry next sync, not be silently accepted as "done". A document with no chunks
            // at all (empty text) still counts as fully processed since there was nothing to embed.
            if (chunks.Count == 0 || anyChunkEmbedded)
                await _contentHashes.SetHashAsync(projectId, sourceRef, hash, ct);
        }

        if (vectorDocs.Count > 0)
            await _vectorStore.UpsertAsync($"{projectId}-kb", vectorDocs, ct);

        if (tokensUsed > 0)
            await _tokenUsage.AddAsync(
                new TokenUsageEntry(projectId, tokensUsed, DateTimeOffset.UtcNow, "ingestion", UserId: triggeredByUserId), ct);

        if (prune is not null)
            await PruneRemovedSourcesAsync(projectId, prune, ct);
    }

    private async Task PruneRemovedSourcesAsync(string projectId, PruneScope prune, CancellationToken ct)
    {
        var knownRefs = await _contentHashes.GetSourceRefsAsync(projectId, ct);
        var staleRefs = knownRefs.Where(r =>
            r.StartsWith(prune.RefPrefix, StringComparison.Ordinal) && !prune.CurrentSourceRefs.Contains(r));

        foreach (var sourceRef in staleRefs)
        {
            await _vectorStore.DeleteByMetadataAsync(
                $"{projectId}-kb", new Dictionary<string, string> { ["source"] = sourceRef }, ct);
            await _contentHashes.DeleteAsync(projectId, sourceRef, ct);
            _logger.LogInformation(
                "Pruned stale KB source '{SourceRef}' for project '{ProjectId}' -- no longer present", sourceRef, projectId);
        }
    }

    private static string ComputeHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Chroma document ids must be stable and collision-free per source; strip characters that
    // don't survive round-tripping through a URL-derived or path-derived sourceRef.
    private static string SanitizeId(string sourceRef) =>
        string.Concat(sourceRef.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

/// <summary>
/// Replaces the identical "find KbSource by Location, stamp LastSyncedAt, upsert" tail duplicated
/// across every KB ingestion job.
/// </summary>
public static class KbSourceSync
{
    public static async Task MarkSyncedAsync(IProjectRepository projects, string projectId, string location, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(projectId, ct);
        if (project is null) return;

        var index = project.KbSources.FindIndex(s => s.Location == location);
        if (index < 0) return;

        project.KbSources[index] = project.KbSources[index] with { LastSyncedAt = DateTimeOffset.UtcNow };
        await projects.UpsertAsync(project, ct);
    }
}
