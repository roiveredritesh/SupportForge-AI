using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Embeddings;

namespace SupportForge.Agents;

public class OpenAiLlmClient : ILlmClient
{
    private readonly IChatClient _chatClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly string _embeddingModel;
    private readonly string? _embeddingInputType;

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
        var response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, userPrompt)],
            cancellationToken: ct);
        LastTotalTokens = (int)(response.Usage?.TotalTokenCount ?? 0);
        return response.Text.Trim();
    }

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

    // ponytail: NIM asymmetric embedding models need input_type "query" vs "passage" depending on
    // whether text is being indexed or searched; this client doesn't distinguish callers, so a single
    // configured type is used for both. Fine for dev/testing; split indexing from search if retrieval
    // quality matters.
    //
    // Uses the protocol-level (raw JSON) embeddings call rather than IEmbeddingGenerator: the OpenAI SDK's
    // typed EmbeddingGenerationOptions silently drops unrecognized fields like NIM's "input_type" instead
    // of forwarding them, so the strongly-typed path can't express this provider-specific parameter.
    public virtual async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _embeddingModel,
            ["input"] = new[] { text },
            ["encoding_format"] = "float",
        };
        if (_embeddingInputType is not null)
            payload["input_type"] = _embeddingInputType;

        var response = await _embeddingClient.GenerateEmbeddingsAsync(
            BinaryContent.Create(BinaryData.FromObjectAsJson(payload)),
            new System.ClientModel.Primitives.RequestOptions { CancellationToken = ct });

        using var doc = JsonDocument.Parse(response.GetRawResponse().Content);
        var vectorJson = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vector = new float[vectorJson.GetArrayLength()];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = vectorJson[i].GetSingle();
        return vector;
    }

    public virtual async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt),
            new DataContent(Convert.FromBase64String(base64Image), "image/png"),
        ]);
        var response = await _chatClient.GetResponseAsync([message], cancellationToken: ct);
        LastTotalTokens = (int)(response.Usage?.TotalTokenCount ?? 0);
        return response.Text.Trim();
    }
}
