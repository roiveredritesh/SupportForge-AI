using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Embeddings;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace SupportForge.Agents;

public class OpenAiLlmClient : ILlmClient
{
    // KTD7/U8: fixed fallback so CompleteAsync is deterministic even when no Seed is configured;
    // any configured value (LlmServiceCollectionExtensions reads "Llm:{Provider}:Seed") overrides it.
    private const long DefaultSeed = 42;

    // Bug fix: CompleteAsync never set a max-output-token cap, so the underlying provider's own
    // server-side default applied silently -- for a NIM-hosted lightweight model that default can be
    // quite small. CodeNodeClassifier's structured multi-file JSON responses were being truncated
    // mid-array well before the model finished (observed live: a single file with 3 definitions cut
    // off after ~441 bytes), causing every classification in the batch to fail to parse and fall back
    // to unenriched. Anthropic/Bedrock already require an explicit MaxTokens (AnthropicOptions/
    // BedrockOptions both default to 4096); this brings the OpenAI-compatible path (OpenAI/NIM/Ollama/
    // Azure) to the same parity instead of relying on an unstated provider default.
    private const int DefaultMaxOutputTokens = 4096;

    private readonly IChatClient _chatClient;
    // Answers image prompts. The chat client unless a separate vision model is configured (Llm:{Provider}:VisionModel),
    // for a text-only chat model such as a large reasoning model.
    private readonly IChatClient _visionChatClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly string _embeddingModel;
    private readonly string? _embeddingInputType;
    private readonly long _seed;
    private readonly int _maxOutputTokens;

    // The raw OpenAI.Chat.ChatClient (and its Azure/NIM variants) is built inline by
    // LlmServiceCollectionExtensions with no HttpClient seam to attach a Polly DelegatingHandler to
    // (see KTD2), so the retry/circuit-breaker pipeline wraps the call site here instead. One pipeline
    // per client instance -- each registered provider (OpenAI/NIM/Azure) gets its own circuit breaker
    // rather than sharing failure state across unrelated endpoints.
    private readonly ResiliencePipeline _resilience;

    // attemptTimeout (Llm:{Provider}:RequestTimeout) cuts one attempt that hangs and lets the retry try again, instead of
    // the request waiting out the SDK's own 100 s network timeout. Unset, attempts are not timed here.
    private static ResiliencePipeline BuildResilience(TimeSpan? attemptTimeout)
    {
        var builder = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientFailure),
                DelayGenerator = args => new ValueTask<TimeSpan?>(
                    args.Outcome.Exception is ClientResultException { Status: 429 } cre
                        ? RateLimitDelay(RetryAfterHeader(cre), args.AttemptNumber)
                        : null),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                // A 429 is the provider's rate limit, not an outage. Counting it opened the breaker during a burst of
                // requests in the code-QA run, and every call then failed for 15 s instead of waiting its turn.
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsTransientFailure(ex) && !IsRateLimited(ex)),
            });
        // Innermost, so each attempt gets its own budget.
        if (attemptTimeout is { } timeout && timeout > TimeSpan.Zero) builder.AddTimeout(timeout);
        return builder.Build();
    }

    private static bool IsRateLimited(Exception ex) => ex is ClientResultException { Status: 429 };

    private static string? RetryAfterHeader(ClientResultException ex)
    {
        string? value = null;
        ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out value);
        return value;
    }

    private static readonly TimeSpan RateLimitBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    // How long to wait after a 429 before retry number attempt + 1: what Retry-After asks (seconds or an HTTP date),
    // capped at 30 s, or 5, 10, 20 s when it is absent or unreadable. A per-minute rate window outlasts the 1, 2, 4 s
    // used for other failures, which is why those retries also got 429.
    public static TimeSpan RateLimitDelay(string? retryAfter, int attempt)
    {
        if (int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds >= MaxRetryAfter.TotalSeconds ? MaxRetryAfter : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var wait = at - DateTimeOffset.UtcNow;
            return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
        }

        return RateLimitBaseDelay * Math.Pow(2, Math.Clamp(attempt, 0, 4));
    }

    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status == 429 || cre.Status >= 500,
        HttpRequestException => true,
        TimeoutException => true,
        TimeoutRejectedException => true,
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
        bool supportsVision = true,
        long? seed = null,
        int? maxOutputTokens = null,
        TimeSpan? attemptTimeout = null,
        IChatClient? visionChatClient = null)
    {
        _resilience = BuildResilience(attemptTimeout);
        _chatClient = chatClient;
        _visionChatClient = visionChatClient ?? chatClient;
        _embeddingClient = embeddingClient;
        _embeddingModel = embeddingModel;
        _embeddingInputType = embeddingInputType;
        SupportsVision = supportsVision;
        _seed = seed ?? DefaultSeed;
        _maxOutputTokens = maxOutputTokens ?? DefaultMaxOutputTokens;
    }

    public virtual async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // KTD7/U8: judge/drafter completions must be deterministic across runs on the same input,
        // so temperature is pinned to 0 and a seed is always passed (StreamCompleteAsync/AnalyzeImageAsync
        // are out of scope -- they weren't flagged as flaky).
        var options = new ChatOptions { Temperature = 0f, Seed = _seed, MaxOutputTokens = _maxOutputTokens };
        var response = await _resilience.ExecuteAsync(
            callback: rct => new ValueTask<ChatResponse>(_chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, userPrompt)],
                options,
                rct)),
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
    public virtual async Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query) =>
        (await EmbedManyAsync([text], ct, purpose))[0];

    // One request for the whole batch: the OpenAI-compatible embeddings API (and NIM's) takes an input array and returns
    // one vector per input, each tagged with its index.
    public virtual async Task<IReadOnlyList<float[]>> EmbedManyAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Passage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _embeddingModel,
            ["input"] = texts,
            ["encoding_format"] = "float",
        };
        if (_embeddingInputType is not null)
            payload["input_type"] = purpose == EmbeddingPurpose.Passage ? "passage" : "query";

        var response = await _embeddingClient.GenerateEmbeddingsAsync(
            BinaryContent.Create(BinaryData.FromObjectAsJson(payload)),
            new System.ClientModel.Primitives.RequestOptions { CancellationToken = ct });

        using var doc = JsonDocument.Parse(response.GetRawResponse().Content);
        var vectors = new float[texts.Count][];
        var position = 0;
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var i) ? i.GetInt32() : position;
            var vectorJson = item.GetProperty("embedding");
            var vector = new float[vectorJson.GetArrayLength()];
            for (var j = 0; j < vector.Length; j++)
                vector[j] = vectorJson[j].GetSingle();
            vectors[index] = vector;
            position++;
        }

        LastTotalTokens = doc.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var total)
            ? total.GetInt32()
            : 0;
        return vectors;
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt),
            new DataContent(Convert.FromBase64String(base64Image), "image/png"),
        ]);
        var response = await _resilience.ExecuteAsync(
            callback: rct => new ValueTask<ChatResponse>(_visionChatClient.GetResponseAsync([message], cancellationToken: rct)),
            cancellationToken: ct);
        LastTotalTokens = (int)(response.Usage?.TotalTokenCount ?? 0);
        return response.Text.Trim();
    }
}
