using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Agents.Tools;

public sealed class KbSearchTool
{
    private readonly ILlmEmbeddingClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly IFeedbackRepository _feedback;

    // U17: lightweight post-query re-ranking, not full re-indexing -- each recorded "not useful"
    // vote against a source nudges its distance score up (Score is a distance: lower = better
    // match), capped so a heavily-downvoted source can still surface if it's genuinely the best
    // match available. Applied here (Agents layer) rather than inside ChromaVectorStoreService so
    // the generic vector-store client stays free of feedback-domain knowledge.
    private const float PenaltyPerNegativeVote = 0.05f;
    private const float MaxPenalty = 0.3f;

    public KbSearchTool(ILlmEmbeddingClient llm, IVectorStoreService vectorStore, IFeedbackRepository feedback)
    {
        _llm = llm;
        _vectorStore = vectorStore;
        _feedback = feedback;
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
            if (filtered.Count > 0) return await ApplyDownweightingAsync(projectId, filtered, ct);
        }

        var results = await _vectorStore.QueryAsync($"{projectId}-kb", embedding, topK, ct: ct);
        return await ApplyDownweightingAsync(projectId, results, ct);
    }

    private async Task<IReadOnlyList<(string Text, string Source)>> ApplyDownweightingAsync(
        string projectId, IReadOnlyList<VectorStore.Models.VectorQueryResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return Map(results);

        var negativeCounts = await GetNegativeCountsBySourceAsync(projectId, ct);
        if (negativeCounts.Count == 0) return Map(results);

        var reranked = results
            .OrderBy(r => r.Score + Penalty(negativeCounts, r.Metadata.GetValueOrDefault("source", "unknown")))
            .ToList();
        return Map(reranked);
    }

    private static float Penalty(IReadOnlyDictionary<string, int> negativeCounts, string source) =>
        negativeCounts.TryGetValue(source, out var count) ? Math.Min(count * PenaltyPerNegativeVote, MaxPenalty) : 0f;

    // ponytail: full scan of every feedback entry on every KB query -- fine at this repo's scale
    // (a JSON file), revisit with a maintained per-source counter if the feedback log grows large.
    private async Task<IReadOnlyDictionary<string, int>> GetNegativeCountsBySourceAsync(string projectId, CancellationToken ct)
    {
        var all = await _feedback.GetAllAsync(ct);
        var counts = new Dictionary<string, int>();
        foreach (var entry in all)
        {
            if (entry.ProjectId != projectId || entry.Useful != false || entry.Sources is null) continue;
            foreach (var source in entry.Sources)
                counts[source] = counts.GetValueOrDefault(source) + 1;
        }
        return counts;
    }

    private static IReadOnlyList<(string Text, string Source)> Map(IReadOnlyList<VectorStore.Models.VectorQueryResult> results) =>
        results.Select(r => (r.Text, r.Metadata.GetValueOrDefault("source", "unknown"))).ToList();
}
