using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Embeddings;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace SupportForge.Agents;

public class OpenAiLlmClient : ILlmClient
{
    private readonly IChatClient _chatClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly string _embeddingModel;
    private readonly string? _embeddingInputType;

    // The raw OpenAI.Chat.ChatClient (and its Azure/NIM variants) is built inline by
    // LlmServiceCollectionExtensions with no HttpClient seam to attach a Polly DelegatingHandler to
    // (see KTD2), so the retry/circuit-breaker pipeline wraps the call site here instead. One pipeline
    // per client instance -- each registered provider (OpenAI/NIM/Azure) gets its own circuit breaker
    // rather than sharing failure state across unrelated endpoints.
    private readonly ResiliencePipeline _resilience = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            UseJitter = true,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientFailure),
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientFailure),
        })
        .Build();

    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status == 429 || cre.Status >= 500,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false,
    };

    public virtual int LastTotalTokens { get; protected set; }

    // OpenAI/NIM/custom-hosted chat models are vision-capable by default; set false in config
    // for a text-only model so CoordinatorPipeline can skip VisionAnalyzerAgent instead of erroring.
    public virtual bool SupportsVision { get; protected set; } = true;

    public OpenAiLlmClient(
        IChatClient chatClient,
        EmbeddingClient embeddingClient,
        string embeddingModel,
        string? embeddingInputType = null,
        bool supportsVision = true)
    {
        _chatClient = chatClient;
        _embeddingClient = embeddingClient;
        _embeddingModel = embeddingModel;
        _embeddingInputType = embeddingInputType;
        SupportsVision = supportsVision;
    }

    public virtual async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var response = await _resilience.ExecuteAsync(
            callback: rct => new ValueTask<ChatResponse>(_chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, userPrompt)],
                cancellationToken: rct)),
            cancellationToken: ct);
        LastTotalTokens = (int)(response.Usage?.TotalTokenCount ?? 0);
        return response.Text.Trim();
    }

    // ponytail: streaming isn't retried -- once chunks start reaching the caller a retry would
    // re-emit duplicate text, so only the non-streaming calls (CompleteAsync/AnalyzeImageAsync) go
    // through the resilience pipeline. Add mid-stream retry (buffer-and-replace before first yield)
    // if streaming failure rate turns out to matter in practice.
    public virtual async IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, userPrompt)],
            cancellationToken: ct))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
        LastTotalTokens = (int)(updates.ToChatResponse().Usage?.TotalTokenCount ?? 0);
    }

    // NIM asymmetric embedding models need input_type "query" vs "passage" depending on whether text
    // is being indexed or searched -- EmbeddingPurpose carries that distinction from the caller.
    // _embeddingInputType configured (non-null) means this provider uses the input_type parameter at
    // all; the actual value sent is derived from purpose, not the configured string.
    //
    // Uses the protocol-level (raw JSON) embeddings call rather than IEmbeddingGenerator: the OpenAI SDK's
    // typed EmbeddingGenerationOptions silently drops unrecognized fields like NIM's "input_type" instead
    // of forwarding them, so the strongly-typed path can't express this provider-specific parameter.
    public virtual async Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _embeddingModel,
            ["input"] = new[] { text },
            ["encoding_format"] = "float",
        };
        if (_embeddingInputType is not null)
            payload["input_type"] = purpose == EmbeddingPurpose.Passage ? "passage" : "query";

        var response = await _embeddingClient.GenerateEmbeddingsAsync(
            BinaryContent.Create(BinaryData.FromObjectAsJson(payload)),
            new System.ClientModel.Primitives.RequestOptions { CancellationToken = ct });

        using var doc = JsonDocument.Parse(response.GetRawResponse().Content);
        var vectorJson = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vector = new float[vectorJson.GetArrayLength()];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = vectorJson[i].GetSingle();

        LastTotalTokens = doc.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var total)
            ? total.GetInt32()
            : 0;
        return vector;
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt),
            new DataContent(Convert.FromBase64String(base64Image), "image/png"),
        ]);
        var response = await _resilience.ExecuteAsync(
            callback: rct => new ValueTask<ChatResponse>(_chatClient.GetResponseAsync([message], cancellationToken: rct)),
            cancellationToken: ct);
        LastTotalTokens = (int)(response.Usage?.TotalTokenCount ?? 0);
        return response.Text.Trim();
    }
}
