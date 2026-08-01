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

    public async Task<IReadOnlyList<(string Text, string Source)>> SearchAsync(string projectId, string query, int topK = 5, CancellationToken ct = default)
    {
        var embedding = await _llm.EmbedAsync(query, ct);
        var results = await _vectorStore.QueryAsync($"{projectId}-kb", embedding, topK, ct: ct);
        return results.Select(r => (r.Text, r.Metadata.GetValueOrDefault("source", "unknown"))).ToList();
    }
}
