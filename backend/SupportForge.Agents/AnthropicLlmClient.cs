using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SupportForge.Agents;

/// <summary>
/// Anthropic's native Messages API is not OpenAI-compatible (message format, system-prompt-as-a-
/// top-level-field, and streaming events all differ), so this talks to it directly via HttpClient
/// rather than reusing <see cref="OpenAiLlmClient"/>. Anthropic ships no embeddings API, which is
/// exactly why the LLM seam was split by capability (see <see cref="ILlmChatClient"/>) -- this class
/// implements chat and vision only.
/// </summary>
public class AnthropicLlmClient : ILlmChatClient
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxTokens;

    public virtual int LastTotalTokens { get; protected set; }
    public virtual bool SupportsVision => true;

    public AnthropicLlmClient(HttpClient http, IOptions<AnthropicOptions> options)
    {
        _http = http;
        var value = options.Value;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(value.BaseUrl);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
        if (!string.IsNullOrEmpty(value.ApiKey))
        {
            _http.DefaultRequestHeaders.Remove("x-api-key");
            _http.DefaultRequestHeaders.Add("x-api-key", value.ApiKey);
        }
        _model = value.ChatModel;
        _maxTokens = value.MaxTokens;
    }

    public virtual async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // KTD7/U8: judge/drafter completions must be deterministic; temperature=0 only (Anthropic
        // has no seed param). StreamCompleteAsync/AnalyzeImageAsync are out of scope -- see U8.
        var payload = BuildRequestBody(systemPrompt, UserTextContent(userPrompt), stream: false, temperature: 0);
        using var response = await _http.PostAsJsonAsync("/v1/messages", payload, ct);
        await EnsureSuccessAsync(response, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        LastTotalTokens = ReadUsageTokens(root);
        return ExtractText(root);
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var content = new object[]
        {
            new { type = "image", source = new { type = "base64", media_type = "image/png", data = base64Image } },
            new { type = "text", text = prompt },
        };
        var payload = BuildRequestBody(systemPrompt: null, content, stream: false);
        using var response = await _http.PostAsJsonAsync("/v1/messages", payload, ct);
        await EnsureSuccessAsync(response, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        LastTotalTokens = ReadUsageTokens(root);
        return ExtractText(root);
    }

    public virtual async IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildRequestBody(systemPrompt, UserTextContent(userPrompt), stream: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(payload),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var inputTokens = 0;
        var outputTokens = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line["data: ".Length..];
            if (json.Length == 0) continue;

            using var eventDoc = JsonDocument.Parse(json);
            var eventRoot = eventDoc.RootElement;
            if (!eventRoot.TryGetProperty("type", out var eventTypeEl)) continue;

            switch (eventTypeEl.GetString())
            {
                case "message_start":
                    if (eventRoot.TryGetProperty("message", out var messageEl)
                        && messageEl.TryGetProperty("usage", out var startUsage)
                        && startUsage.TryGetProperty("input_tokens", out var inputEl))
                        inputTokens = inputEl.GetInt32();
                    break;
                case "content_block_delta":
                    if (eventRoot.TryGetProperty("delta", out var deltaEl)
                        && deltaEl.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                            yield return text;
                    }
                    break;
                case "message_delta":
                    if (eventRoot.TryGetProperty("usage", out var deltaUsage)
                        && deltaUsage.TryGetProperty("output_tokens", out var outputEl))
                        outputTokens = outputEl.GetInt32();
                    break;
            }
        }

        LastTotalTokens = inputTokens + outputTokens;
    }

    private object BuildRequestBody(string? systemPrompt, object content, bool stream, int? temperature = null)
    {
        var messages = new[] { new { role = "user", content } };
        if (temperature is { } t)
        {
            return systemPrompt is null
                ? new { model = _model, max_tokens = _maxTokens, temperature = t, messages, stream }
                : new { model = _model, max_tokens = _maxTokens, temperature = t, system = systemPrompt, messages, stream };
        }
        return systemPrompt is null
            ? new { model = _model, max_tokens = _maxTokens, messages, stream }
            : new { model = _model, max_tokens = _maxTokens, system = systemPrompt, messages, stream };
    }

    private static string UserTextContent(string text) => text;

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var contentEl) || contentEl.GetArrayLength() == 0)
            return string.Empty;
        var sb = new StringBuilder();
        foreach (var block in contentEl.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text"
                && block.TryGetProperty("text", out var textEl))
                sb.Append(textEl.GetString());
        }
        return sb.ToString().Trim();
    }

    private static int ReadUsageTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl)) return 0;
        var input = usageEl.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
        var output = usageEl.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
        return input + output;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        // Capped rather than embedded verbatim: the body can echo back request-derived context
        // (e.g. a validation error quoting part of the offending prompt), and this message can
        // reach application logs via generic exception logging.
        var truncated = body.Length > 500 ? body[..500] + "... (truncated)" : body;
        throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode} {response.ReasonPhrase}: {truncated}", null, response.StatusCode);
    }
}
