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

    public KbVectorIndexer(ILlmEmbeddingClient llm, IVectorStoreService vectorStore)
    {
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task IndexAsync(string projectId, IEnumerable<(string SourceRef, string Text)> documents, CancellationToken ct)
    {
        var vectorDocs = new List<VectorDocument>();
        foreach (var (sourceRef, text) in documents)
        {
            var chunks = DocumentChunker.Chunk(text);
            for (var i = 0; i < chunks.Count; i++)
            {
                var embedding = await _llm.EmbedAsync(chunks[i], ct);
                vectorDocs.Add(new VectorDocument(
                    Id: $"{SanitizeId(sourceRef)}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: new Dictionary<string, string> { ["source"] = sourceRef, ["chunk"] = i.ToString() }));
            }
        }

        if (vectorDocs.Count > 0)
            await _vectorStore.UpsertAsync($"{projectId}-kb", vectorDocs, ct);
    }

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
