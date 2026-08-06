using System.Security.Cryptography;
using System.Text;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Documents;

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

    public KbVectorIndexer(ILlmEmbeddingClient llm, IVectorStoreService vectorStore, IContentHashRepository contentHashes)
    {
        _llm = llm;
        _vectorStore = vectorStore;
        _contentHashes = contentHashes;
    }

    // D1 (gap-closing-solutions.md Phase D, item 1): Title is optional richer metadata (Confluence
    // page title, Website <title>) attached per chunk when the source format provides one -- null
    // for sources with no natural title distinct from their SourceRef (Documents file paths).
    public async Task IndexAsync(string projectId, IEnumerable<(string SourceRef, string Text, string? Title)> documents, CancellationToken ct)
    {
        var vectorDocs = new List<VectorDocument>();
        foreach (var (sourceRef, text, title) in documents)
        {
            // D1: unchanged content since the last successful index is skipped entirely -- no
            // re-chunk, no re-embed, no upsert call. A document that changes even slightly still
            // gets fully re-processed; this only saves work for genuinely untouched sources.
            var hash = ComputeHash(text);
            var previousHash = await _contentHashes.GetHashAsync(projectId, sourceRef, ct);
            if (previousHash == hash) continue;

            var chunks = DocumentChunker.Chunk(text);
            for (var i = 0; i < chunks.Count; i++)
            {
                var embedding = await _llm.EmbedAsync(chunks[i], ct, EmbeddingPurpose.Passage);
                var metadata = new Dictionary<string, string> { ["source"] = sourceRef, ["chunk"] = i.ToString() };
                if (!string.IsNullOrWhiteSpace(title)) metadata["title"] = title;
                vectorDocs.Add(new VectorDocument(
                    Id: $"{SanitizeId(sourceRef)}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: metadata));
            }

            await _contentHashes.SetHashAsync(projectId, sourceRef, hash, ct);
        }

        if (vectorDocs.Count > 0)
            await _vectorStore.UpsertAsync($"{projectId}-kb", vectorDocs, ct);
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
