using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SupportForge.Agents;

public class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _http;

    public int LastTotalTokens { get; private set; }

    public OpenAiLlmClient(HttpClient http) => _http = http;

    public virtual async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
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
        LastTotalTokens = body?.Usage?.TotalTokens ?? 0;
        return body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    public virtual async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("embeddings", new { model = "text-embedding-3-small", input = text }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);
        return body?.Data.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("chat/completions", new
        {
            model = "gpt-4o-mini",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } },
                    },
                },
            },
        }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        LastTotalTokens = body?.Usage?.TotalTokens ?? 0;
        return body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    private sealed class ChatResponse { public List<Choice> Choices { get; set; } = new(); public Usage? Usage { get; set; } }
    private sealed class Choice { public Message Message { get; set; } = new(); }
    private sealed class Message { public string? Content { get; set; } }
    private sealed class Usage { [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; } }
    private sealed class EmbeddingResponse { public List<EmbeddingData> Data { get; set; } = new(); }
    private sealed class EmbeddingData { public float[] Embedding { get; set; } = Array.Empty<float>(); }
}
