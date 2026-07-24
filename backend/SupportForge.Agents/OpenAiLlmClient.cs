using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SupportForge.Agents;

public class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly string? _embeddingInputType;

    public virtual int LastTotalTokens { get; protected set; }

    public OpenAiLlmClient(HttpClient http, string chatModel = "gpt-4o-mini", string embeddingModel = "text-embedding-3-small", string? embeddingInputType = null)
    {
        _http = http;
        _chatModel = chatModel;
        _embeddingModel = embeddingModel;
        _embeddingInputType = embeddingInputType;
    }

    public virtual async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("chat/completions", new
        {
            model = _chatModel,
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
        // ponytail: NIM asymmetric embedding models need input_type "query" vs "passage" depending on
        // whether text is being indexed or searched; this client doesn't distinguish callers, so a single
        // configured type is used for both. Fine for dev/testing; split indexing from search if retrieval
        // quality matters.
        object payload = _embeddingInputType is null
            ? new { model = _embeddingModel, input = text }
            : new { model = _embeddingModel, input = text, input_type = _embeddingInputType, encoding_format = "float" };
        var response = await _http.PostAsJsonAsync("embeddings", payload, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);
        return body?.Data.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("chat/completions", new
        {
            model = _chatModel,
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
