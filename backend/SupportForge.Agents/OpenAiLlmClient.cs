using System.Net.Http.Json;

namespace SupportForge.Agents;

public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _http;

    public OpenAiLlmClient(HttpClient http) => _http = http;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("chat/completions", new
        {
            model = "gpt-4o-mini",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        return body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("embeddings", new { model = "text-embedding-3-small", input = text }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);
        return body?.Data.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
    }

    private sealed class ChatResponse { public List<Choice> Choices { get; set; } = new(); }
    private sealed class Choice { public Message Message { get; set; } = new(); }
    private sealed class Message { public string? Content { get; set; } }
    private sealed class EmbeddingResponse { public List<EmbeddingData> Data { get; set; } = new(); }
    private sealed class EmbeddingData { public float[] Embedding { get; set; } = Array.Empty<float>(); }
}
