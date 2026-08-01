namespace SupportForge.Agents;

public interface ILlmChatClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamCompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default);
    bool SupportsVision { get; }
    int LastTotalTokens { get; }
}

public interface ILlmEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    int LastTotalTokens { get; }
}

// Anthropic ships no embeddings API, so a provider can implement ILlmChatClient without
// ILlmEmbeddingClient (chat via Anthropic + embeddings via a different provider). ILlmClient
// remains the union for the common case (OpenAI/NIM/custom-hosted) where one provider does both.
public interface ILlmClient : ILlmChatClient, ILlmEmbeddingClient
{
    // Redeclared to disambiguate the diamond: both parents declare LastTotalTokens, and a single
    // implementation (e.g. OpenAiLlmClient) satisfies all three with one backing property.
    new int LastTotalTokens { get; }
}
