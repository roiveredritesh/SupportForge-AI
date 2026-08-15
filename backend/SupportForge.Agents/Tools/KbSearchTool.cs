using SupportForge.VectorStore;

namespace SupportForge.Agents.Tools;

public sealed class KbSearchTool
{
    private readonly ILlmEmbeddingClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public KbSearchTool(ILlmEmbeddingClient llm, IVectorStoreService vectorStore)
    {
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task<IReadOnlyList<(string Text, string Source)>> SearchAsync(
        string projectId, string query, int topK = 5,
        IReadOnlyDictionary<string, string>? metadataFilter = null, CancellationToken ct = default)
    {
        var embedding = await _llm.EmbedAsync(query, ct, EmbeddingPurpose.Query);

        // U10: metadataFilter is a hard equality filter (Chroma "where" / Pinecone $eq), and most
        // KB content isn't version-tagged today -- filtering first and falling back to an unfiltered
        // query on zero results gives "prefer version-matched sources when they exist" without
        // silently returning nothing for the common untagged case.
        if (metadataFilter is { Count: > 0 })
        {
            var filtered = await _vectorStore.QueryAsync($"{projectId}-kb", embedding, topK, metadataFilter, ct);
            if (filtered.Count > 0) return Map(filtered);
        }

        var results = await _vectorStore.QueryAsync($"{projectId}-kb", embedding, topK, ct: ct);
        return Map(results);
    }

    private static IReadOnlyList<(string Text, string Source)> Map(IReadOnlyList<VectorStore.Models.VectorQueryResult> results) =>
        results.Select(r => (r.Text, r.Metadata.GetValueOrDefault("source", "unknown"))).ToList();
}
