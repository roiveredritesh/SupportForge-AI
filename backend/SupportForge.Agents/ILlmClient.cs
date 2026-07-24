namespace SupportForge.Agents;

public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default);
}
