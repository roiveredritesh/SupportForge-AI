namespace SupportForge.Agents;

public interface ILlmChatClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamCompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default);
    bool SupportsVision { get; }
    int LastTotalTokens { get; }
}

// NIM's asymmetric embedding models (e.g. nv-embedqa-e5-v5) encode "what am I looking for" and
// "what does this document contain" into different subspaces -- embedding both sides the same way
// measurably degrades nearest-neighbor ranking. Query is the default (matches every pre-existing
// two-arg call site, all of which were search-query embeds); callers indexing documents must pass
// Passage explicitly.
public enum EmbeddingPurpose { Query, Passage }

public interface ILlmEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query);
    int LastTotalTokens { get; }
}

// Anthropic ships no embeddings API, so a provider can implement ILlmChatClient without
// ILlmEmbeddingClient (chat via Anthropic + embeddings via a different provider). ILlmClient
// remains the union for the common case (OpenAI/NIM/custom-hosted) where one provider does both.
public interface ILlmClient : ILlmChatClient, ILlmEmbeddingClient
{
}
