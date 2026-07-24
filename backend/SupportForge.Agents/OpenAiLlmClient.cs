using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportForge.Agents;

public class OpenAiLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public virtual async IAsyncEnumerable<string> StreamCompleteAsync(string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = _chatModel,
                stream = true,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            }),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var totalTokens = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            var chunk = JsonSerializer.Deserialize<StreamChunk>(data, JsonOptions);
            if (chunk?.Usage is { } usage) totalTokens = usage.TotalTokens;

            var delta = chunk?.Choices.FirstOrDefault()?.Delta.Content;
            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
        LastTotalTokens = totalTokens;
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
    private sealed class StreamChunk { public List<StreamChoice> Choices { get; set; } = new(); public Usage? Usage { get; set; } }
    private sealed class StreamChoice { public StreamDelta Delta { get; set; } = new(); }
    private sealed class StreamDelta { public string? Content { get; set; } }
}
