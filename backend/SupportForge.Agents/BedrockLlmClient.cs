using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Microsoft.Extensions.Options;

namespace SupportForge.Agents;

/// <summary>
/// AWS Bedrock has a different wire format and SigV4 auth handled entirely by <see cref="IAmazonBedrockRuntime"/>
/// (the AWS SDK), so this needs no HttpClient of its own. Request/response bodies here target
/// Claude-on-Bedrock's message format (the most common Bedrock chat model family) for chat/vision,
/// and Titan Embeddings' body shape for embeddings -- both are Bedrock-specific, not
/// OpenAI-compatible, which is why this can't reuse <see cref="OpenAiLlmClient"/>.
/// </summary>
public class BedrockLlmClient : ILlmClient
{
    private const string AnthropicBedrockVersion = "bedrock-2023-05-31";

    private readonly IAmazonBedrockRuntime _bedrock;
    private readonly string _chatModelId;
    private readonly string _embeddingModelId;
    private readonly int _maxTokens;

    public virtual int LastTotalTokens { get; protected set; }
    public virtual bool SupportsVision => true;

    public BedrockLlmClient(IAmazonBedrockRuntime bedrock, IOptions<BedrockOptions> options)
    {
        _bedrock = bedrock;
        _chatModelId = options.Value.ChatModel;
        _embeddingModelId = options.Value.EmbeddingModel;
        _maxTokens = options.Value.MaxTokens;
    }

    public virtual async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var body = BuildMessageBody(systemPrompt, userPrompt);
        var (text, tokens) = await InvokeAsync(_chatModelId, body, ct);
        LastTotalTokens = tokens;
        return text;
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var content = new object[]
        {
            new { type = "image", source = new { type = "base64", media_type = "image/png", data = base64Image } },
            new { type = "text", text = prompt },
        };
        var body = BuildMessageBody(systemPrompt: null, content);
        var (text, tokens) = await InvokeAsync(_chatModelId, body, ct);
        LastTotalTokens = tokens;
        return text;
    }

    public virtual async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = new { inputText = text };
        var request = new InvokeModelRequest
        {
            ModelId = _embeddingModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = ToStream(body),
        };
        var response = await _bedrock.InvokeModelAsync(request, ct);

        using var doc = await JsonDocument.ParseAsync(response.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var embeddingEl = root.GetProperty("embedding");
        var vector = new float[embeddingEl.GetArrayLength()];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = embeddingEl[i].GetSingle();

        LastTotalTokens = root.TryGetProperty("inputTextTokenCount", out var tokenCount) ? tokenCount.GetInt32() : 0;
        return vector;
    }

    public virtual async IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildMessageBody(systemPrompt, userPrompt);
        var request = new InvokeModelWithResponseStreamRequest
        {
            ModelId = _chatModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = ToStream(body),
        };

        var response = await _bedrock.InvokeModelWithResponseStreamAsync(request, ct);
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var inputTokens = 0;
        var outputTokens = 0;

        void OnChunk(object? sender, EventStreamEventReceivedArgs<PayloadPart> e)
        {
            try
            {
                using var chunkDoc = JsonDocument.Parse(e.EventStreamEvent.Bytes);
                var chunkRoot = chunkDoc.RootElement;
                if (!chunkRoot.TryGetProperty("type", out var typeEl)) return;
                switch (typeEl.GetString())
                {
                    case "message_start":
                        if (chunkRoot.TryGetProperty("message", out var msg)
                            && msg.TryGetProperty("usage", out var startUsage)
                            && startUsage.TryGetProperty("input_tokens", out var inputEl))
                            inputTokens = inputEl.GetInt32();
                        break;
                    case "content_block_delta":
                        if (chunkRoot.TryGetProperty("delta", out var deltaEl)
                            && deltaEl.TryGetProperty("text", out var textEl))
                            channel.Writer.TryWrite(textEl.GetString() ?? string.Empty);
                        break;
                    case "message_delta":
                        if (chunkRoot.TryGetProperty("usage", out var deltaUsage)
                            && deltaUsage.TryGetProperty("output_tokens", out var outputEl))
                            outputTokens = outputEl.GetInt32();
                        break;
                }
            }
            catch (Exception ex)
            {
                // The AWS SDK may not surface a handler exception back through StartProcessing(),
                // so fault the channel directly here as well as letting it propagate below --
                // otherwise a malformed chunk would silently truncate the stream instead of erroring.
                channel.Writer.TryComplete(ex);
            }
        }

        response.Body.ChunkReceived += OnChunk;
        // Forward the pump's fault (if any) into the channel instead of the parameterless
        // TryComplete(), which always marks the channel as successfully finished -- without this,
        // a mid-stream parse/SDK error looked identical to a clean stream end, silently truncating
        // the reply and under-reporting token usage.
        var pump = Task.Run(() => response.Body.StartProcessing(), ct)
            .ContinueWith(t => channel.Writer.TryComplete(t.IsFaulted ? t.Exception!.GetBaseException() : null), CancellationToken.None);

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
                yield return chunk;
        }
        finally
        {
            // Always observe the pump so its exception (if any) isn't left unobserved, and so a
            // caller cancellation that unwinds the foreach above doesn't leak the background pump.
            try { await pump; } catch (OperationCanceledException) { }
        }

        LastTotalTokens = inputTokens + outputTokens;
    }

    private object BuildMessageBody(string? systemPrompt, object content)
    {
        var messages = new[] { new { role = "user", content } };
        return systemPrompt is null
            ? new { anthropic_version = AnthropicBedrockVersion, max_tokens = _maxTokens, messages }
            : new { anthropic_version = AnthropicBedrockVersion, max_tokens = _maxTokens, system = systemPrompt, messages };
    }

    private async Task<(string Text, int Tokens)> InvokeAsync(string modelId, object body, CancellationToken ct)
    {
        var request = new InvokeModelRequest
        {
            ModelId = modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = ToStream(body),
        };
        var response = await _bedrock.InvokeModelAsync(request, ct);

        using var doc = await JsonDocument.ParseAsync(response.Body, cancellationToken: ct);
        var root = doc.RootElement;

        var text = string.Empty;
        if (root.TryGetProperty("content", out var contentEl) && contentEl.GetArrayLength() > 0)
        {
            var sb = new StringBuilder();
            foreach (var block in contentEl.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text"
                    && block.TryGetProperty("text", out var textEl))
                    sb.Append(textEl.GetString());
            }
            text = sb.ToString().Trim();
        }

        var tokens = 0;
        if (root.TryGetProperty("usage", out var usageEl))
        {
            var input = usageEl.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
            var output = usageEl.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            tokens = input + output;
        }

        return (text, tokens);
    }

    private static MemoryStream ToStream(object body) => new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
}
