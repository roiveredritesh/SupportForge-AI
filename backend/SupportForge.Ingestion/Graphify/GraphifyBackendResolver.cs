using Microsoft.Extensions.Configuration;

namespace SupportForge.Ingestion.Graphify;

/// <summary>
/// Derives the environment variables graphify's semantic extraction reads for its own
/// <c>--backend</c> selection (gemini|kimi|claude|openai|deepseek|ollama -- a second, independent
/// provider surface from the app's own Llm:* config) from the app's configured chat provider, so an
/// operator configures a provider once (KTD3). Bedrock and Azure are not graphify backends, so those
/// require an OpenAI-compatible gateway (KTD2) -- <see cref="Resolve"/> fails loudly here, at
/// configuration time, rather than letting the first `graphify extract` call fail obscurely.
/// </summary>
public static class GraphifyBackendResolver
{
    public static IReadOnlyDictionary<string, string?> Resolve(IConfiguration configuration)
    {
        var provider = configuration["Llm:Provider"];
        provider = string.IsNullOrWhiteSpace(provider) ? "OpenAI" : provider;
        return provider switch
        {
            "OpenAI" or "NvidiaNim" => ResolveOpenAiCompatible(configuration, provider),
            "Anthropic" => ResolveAnthropic(configuration),
            "Azure" or "Bedrock" => ResolveViaGateway(configuration, provider),
            _ => throw new InvalidOperationException(
                $"Llm:Provider '{provider}' has no graphify backend derivation. Supported: OpenAI, NvidiaNim, Azure, Anthropic, Bedrock."),
        };
    }

    private static IReadOnlyDictionary<string, string?> ResolveOpenAiCompatible(IConfiguration configuration, string provider)
    {
        var section = configuration.GetSection($"Llm:{provider}");
        return new Dictionary<string, string?>
        {
            ["OPENAI_BASE_URL"] = section["BaseUrl"] ?? "https://api.openai.com/v1/",
            ["OPENAI_MODEL"] = section["ChatModel"] ?? "gpt-4o-mini",
            ["OPENAI_API_KEY"] = section["ApiKey"] ?? configuration["Llm:ApiKey"] ?? configuration["OpenAI:ApiKey"],
        };
    }

    private static IReadOnlyDictionary<string, string?> ResolveAnthropic(IConfiguration configuration)
    {
        var section = configuration.GetSection("Llm:Anthropic");
        return new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = section["BaseUrl"] ?? "https://api.anthropic.com",
            ["ANTHROPIC_MODEL"] = section["ChatModel"] ?? "claude-sonnet-4-20250514",
            ["ANTHROPIC_API_KEY"] = section["ApiKey"],
        };
    }

    private static IReadOnlyDictionary<string, string?> ResolveViaGateway(IConfiguration configuration, string provider)
    {
        var gateway = configuration.GetSection("Graphify:Gateway");
        var gatewayUrl = gateway["BaseUrl"];

        if (string.IsNullOrEmpty(gatewayUrl))
            throw new InvalidOperationException(
                $"Llm:Provider '{provider}' is not a graphify --backend (graphify supports gemini|kimi|claude|openai|deepseek|ollama, not Bedrock or Azure directly). " +
                "Configure Graphify:Gateway:BaseUrl and Graphify:Gateway:ApiKey to front it with an OpenAI-compatible gateway (e.g. LiteLLM), or graphify's semantic extraction (docs/Confluence/website ingestion) cannot run.");

        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"Graphify:Gateway:BaseUrl '{gatewayUrl}' must be an HTTPS URL -- the gateway is a trust boundary graphify's extraction output is taken on faith from.");

        var gatewayApiKey = gateway["ApiKey"];
        if (string.IsNullOrEmpty(gatewayApiKey))
            throw new InvalidOperationException("Graphify:Gateway:ApiKey is required when fronting Bedrock/Azure with an OpenAI-compatible gateway.");

        return new Dictionary<string, string?>
        {
            ["OPENAI_BASE_URL"] = gatewayUrl,
            ["OPENAI_MODEL"] = gateway["Model"] ?? configuration[$"Llm:{provider}:ChatModel"],
            ["OPENAI_API_KEY"] = gatewayApiKey,
        };
    }
}
