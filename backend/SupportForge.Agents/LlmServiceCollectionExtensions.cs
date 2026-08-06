using Amazon;
using Amazon.BedrockRuntime;
using System.ClientModel;
using System.Net;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using OpenAI;
using Polly;

namespace SupportForge.Agents;

/// <summary>
/// Wires the app's LLM provider config ("Llm:Provider" for chat/vision, "Embeddings:Provider" for
/// embeddings -- defaulting to the same provider as chat) into the split ILlmChatClient /
/// ILlmEmbeddingClient seams. Mirrors <c>AddVectorStore</c>'s provider-selects-a-branch pattern.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    private static readonly string[] SupportedProviders = { "OpenAI", "NvidiaNim", "Ollama", "Azure", "Anthropic", "Bedrock" };

    // Model tiering (gap-closing-solutions.md Phase C, item 3): the key under which a second,
    // cheap-tier ILlmChatClient is registered alongside the default one. Resolve via
    // sp.GetRequiredKeyedService<ILlmChatClient>(CheapTierKey) for classifier/judge-style calls
    // (Triage, the three verifiers) where a smaller model is an acceptable quality/cost trade --
    // see docs/architecture/2026-08-06-003-gap-closing-solutions.md Phase C item 3. Every provider's
    // *ChatModel config falls back to the main ChatModel when unset, so tiering is opt-in per
    // deployment: registering this key changes nothing until a CheapChatModel is actually configured.
    public const string CheapTierKey = "cheap";

    public static IServiceCollection AddLlmProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var chatProvider = NullIfBlank(configuration["Llm:Provider"]) ?? "OpenAI";
        // A JSON `null` config value can surface as an empty string depending on provider/binding
        // path, so blank is treated the same as absent (not a request for a blank-named provider).
        var embeddingProvider = NullIfBlank(configuration["Embeddings:Provider"]) ?? chatProvider;
        var sameProvider = string.Equals(chatProvider, embeddingProvider, StringComparison.OrdinalIgnoreCase);

        RegisterChatProvider(services, configuration, chatProvider);
        RegisterCheapChatProvider(services, configuration, chatProvider);

        if (sameProvider)
        {
            if (!ProviderSupportsEmbeddings(chatProvider))
                throw new InvalidOperationException(
                    $"Llm:Provider '{chatProvider}' has no embeddings API. Configure Embeddings:Provider to a provider that does ({string.Join(", ", SupportedProviders.Where(ProviderSupportsEmbeddings))}).");

            services.AddSingleton<ILlmEmbeddingClient>(sp => (ILlmEmbeddingClient)sp.GetRequiredService<ILlmChatClient>());
            services.AddSingleton<ILlmClient>(sp => (ILlmClient)sp.GetRequiredService<ILlmChatClient>());
        }
        else
        {
            if (!ProviderSupportsEmbeddings(embeddingProvider))
                throw new InvalidOperationException(
                    $"Embeddings:Provider '{embeddingProvider}' has no embeddings API. Supported embeddings providers: {string.Join(", ", SupportedProviders.Where(ProviderSupportsEmbeddings))}.");

            RegisterEmbeddingProvider(services, configuration, embeddingProvider);
            // No ILlmClient union when chat and embeddings come from different providers/instances
            // -- callers needing both must depend on the narrower ILlmChatClient/ILlmEmbeddingClient seams.
        }

        return services;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool ProviderSupportsEmbeddings(string provider) =>
        !string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase);

    private static void RegisterChatProvider(IServiceCollection services, IConfiguration configuration, string provider)
    {
        switch (provider)
        {
            case "OpenAI":
            case "NvidiaNim":
            case "Ollama":
                services.AddSingleton<ILlmChatClient>(_ => BuildOpenAiCompatibleClient(configuration, provider));
                break;
            case "Azure":
                services.AddSingleton<ILlmChatClient>(_ => BuildAzureClient(configuration));
                break;
            case "Anthropic":
                services.Configure<AnthropicOptions>(configuration.GetSection("Llm:Anthropic"));
                // HttpClient.Timeout bounds the whole request including reading the body -- the
                // 100s default would tear down a long-running SSE stream mid-generation.
                services.AddHttpClient<AnthropicLlmClient>(c => c.Timeout = TimeSpan.FromMinutes(5))
                    .AddResilienceHandler("llm-retry", AddLlmResilience);
                services.AddSingleton<ILlmChatClient>(sp => sp.GetRequiredService<AnthropicLlmClient>());
                break;
            case "Bedrock":
                EnsureBedrockRuntimeRegistered(services, configuration);
                services.Configure<BedrockOptions>(configuration.GetSection("Llm:Bedrock"));
                services.AddSingleton<BedrockLlmClient>();
                services.AddSingleton<ILlmChatClient>(sp => sp.GetRequiredService<BedrockLlmClient>());
                break;
            default:
                throw new InvalidOperationException($"Llm:Provider '{provider}' is not recognized. Supported: {string.Join(", ", SupportedProviders)}.");
        }
    }

    // Registers the same provider a second time under CheapTierKey, using each provider's
    // *CheapChatModel config (falling back to its normal ChatModel -- see CheapTierKey doc comment).
    // Embeddings are never tiered: only chat/judge calls (Triage, verifiers) use this key.
    private static void RegisterCheapChatProvider(IServiceCollection services, IConfiguration configuration, string provider)
    {
        switch (provider)
        {
            case "OpenAI":
            case "NvidiaNim":
            case "Ollama":
                services.AddKeyedSingleton<ILlmChatClient>(CheapTierKey,
                    (_, _) => BuildOpenAiCompatibleClient(configuration, provider, cheapTier: true));
                break;
            case "Azure":
                services.AddKeyedSingleton<ILlmChatClient>(CheapTierKey, (_, _) => BuildAzureClient(configuration, cheapTier: true));
                break;
            case "Anthropic":
                services.AddKeyedSingleton<ILlmChatClient>(CheapTierKey, (sp, _) =>
                {
                    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value;
                    var cheapOptions = Microsoft.Extensions.Options.Options.Create(new AnthropicOptions
                    {
                        BaseUrl = options.BaseUrl,
                        ChatModel = options.CheapChatModel ?? options.ChatModel,
                        ApiKey = options.ApiKey,
                        MaxTokens = options.MaxTokens,
                    });
                    // Reuses the same named+configured HttpClient (timeout, resilience handler) the
                    // default AnthropicLlmClient registration already set up -- only the model differs.
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AnthropicLlmClient));
                    return new AnthropicLlmClient(http, cheapOptions);
                });
                break;
            case "Bedrock":
                services.AddKeyedSingleton<ILlmChatClient>(CheapTierKey, (sp, _) =>
                {
                    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BedrockOptions>>().Value;
                    var cheapOptions = Microsoft.Extensions.Options.Options.Create(new BedrockOptions
                    {
                        Region = options.Region,
                        ChatModel = options.CheapChatModel ?? options.ChatModel,
                        EmbeddingModel = options.EmbeddingModel,
                        MaxTokens = options.MaxTokens,
                    });
                    return new BedrockLlmClient(sp.GetRequiredService<IAmazonBedrockRuntime>(), cheapOptions);
                });
                break;
            default:
                throw new InvalidOperationException($"Llm:Provider '{provider}' is not recognized. Supported: {string.Join(", ", SupportedProviders)}.");
        }
    }

    private static void RegisterEmbeddingProvider(IServiceCollection services, IConfiguration configuration, string provider)
    {
        switch (provider)
        {
            case "OpenAI":
            case "NvidiaNim":
            case "Ollama":
                services.AddSingleton<ILlmEmbeddingClient>(_ => BuildOpenAiCompatibleClient(configuration, provider));
                break;
            case "Azure":
                services.AddSingleton<ILlmEmbeddingClient>(_ => BuildAzureClient(configuration));
                break;
            case "Bedrock":
                EnsureBedrockRuntimeRegistered(services, configuration);
                services.Configure<BedrockOptions>(configuration.GetSection("Llm:Bedrock"));
                services.AddSingleton<ILlmEmbeddingClient>(sp =>
                    new BedrockLlmClient(sp.GetRequiredService<IAmazonBedrockRuntime>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BedrockOptions>>()));
                break;
            default:
                throw new InvalidOperationException($"Embeddings:Provider '{provider}' is not recognized. Supported: {string.Join(", ", SupportedProviders.Where(ProviderSupportsEmbeddings))}.");
        }
    }

    private static void EnsureBedrockRuntimeRegistered(IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(sd => sd.ServiceType == typeof(IAmazonBedrockRuntime))) return;
        services.AddSingleton<IAmazonBedrockRuntime>(_ =>
        {
            var region = configuration["Llm:Bedrock:Region"] ?? "us-east-1";
            // The AWS SDK owns its own retry/backoff machinery (RetryMode.Standard = exponential
            // backoff with jitter) -- Polly doesn't attach here since there's no HttpClient seam,
            // see KTD2 in the mitigation plan.
            var config = new AmazonBedrockRuntimeConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                RetryMode = Amazon.Runtime.RequestRetryMode.Standard,
                MaxErrorRetry = 3,
            };
            return new AmazonBedrockRuntimeClient(config);
        });
    }

    // Shared retry+circuit-breaker shape for the one LLM client with a real HttpClient seam
    // (Anthropic). Bounded exponential backoff (~3 attempts) then a circuit breaker that opens on
    // sustained failure so a down provider gets failed fast instead of hammered -- see U2/KTD2.
    private static void AddLlmResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            UseJitter = true,
        });
        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
        });
    }

    private static OpenAiLlmClient BuildOpenAiCompatibleClient(IConfiguration configuration, string providerSection, bool cheapTier = false)
    {
        var section = configuration.GetSection($"Llm:{providerSection}");
        var isOllama = providerSection == "Ollama";
        var baseUrl = section["BaseUrl"] ?? (isOllama ? "http://localhost:11434/v1/" : "https://api.openai.com/v1/");
        var apiKey = section["ApiKey"] ?? configuration["Llm:ApiKey"] ?? configuration["OpenAI:ApiKey"];
        var chatModel = section["ChatModel"] ?? (isOllama ? "llama3.1" : "gpt-4o-mini");
        // Model tiering (gap-closing-solutions.md Phase C, item 3): falls back to the normal
        // ChatModel when CheapChatModel is unset, so this is a no-op change unless configured.
        if (cheapTier) chatModel = section["CheapChatModel"] ?? chatModel;
        var embeddingModel = section["EmbeddingModel"] ?? (isOllama ? "nomic-embed-text" : "text-embedding-3-small");
        var embeddingInputType = section["EmbeddingInputType"];

        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        // Ollama's server ignores the Authorization header entirely (no auth), but the OpenAI SDK's
        // ApiKeyCredential still requires a non-empty string client-side -- "ollama" is the value
        // Ollama's own docs use as a placeholder for OpenAI-SDK-compatible clients.
        var credential = new ApiKeyCredential(!string.IsNullOrEmpty(apiKey) ? apiKey : isOllama ? "ollama" : string.Empty);
        return new OpenAiLlmClient(
            new OpenAI.Chat.ChatClient(chatModel, credential, options).AsIChatClient(),
            new OpenAI.Embeddings.EmbeddingClient(embeddingModel, credential, options),
            embeddingModel,
            embeddingInputType);
    }

    private static OpenAiLlmClient BuildAzureClient(IConfiguration configuration, bool cheapTier = false)
    {
        // Azure.AI.OpenAI's GetChatClient/GetEmbeddingClient return the same OpenAI SDK types
        // OpenAiLlmClient already wraps, so Azure needs no new ILlmClient implementation -- only a
        // different client construction path (endpoint + api-key auth instead of a plain BaseUrl).
        var section = configuration.GetSection("Llm:Azure");
        var endpoint = section["BaseUrl"] ?? throw new InvalidOperationException("Llm:Azure:BaseUrl (the Azure OpenAI resource endpoint) is required.");
        var apiKey = section["ApiKey"] ?? configuration["Llm:ApiKey"];
        var chatDeployment = section["ChatModel"] ?? throw new InvalidOperationException("Llm:Azure:ChatModel (the chat deployment name) is required.");
        // Model tiering (gap-closing-solutions.md Phase C, item 3): falls back to the normal
        // deployment when CheapChatModel is unset -- Azure deployment names are provider-assigned,
        // so a cheap tier here requires a separate deployment to already exist.
        if (cheapTier) chatDeployment = section["CheapChatModel"] ?? chatDeployment;
        var embeddingDeployment = section["EmbeddingModel"] ?? throw new InvalidOperationException("Llm:Azure:EmbeddingModel (the embedding deployment name) is required.");
        var embeddingInputType = section["EmbeddingInputType"];

        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey ?? string.Empty));
        return new OpenAiLlmClient(
            azureClient.GetChatClient(chatDeployment).AsIChatClient(),
            azureClient.GetEmbeddingClient(embeddingDeployment),
            embeddingDeployment,
            embeddingInputType);
    }
}
