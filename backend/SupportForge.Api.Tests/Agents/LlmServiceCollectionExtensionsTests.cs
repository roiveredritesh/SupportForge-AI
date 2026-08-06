using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class LlmServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddLlmProviders_DefaultsToOpenAi_AndRegistersUnionClient()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:OpenAI:ApiKey"] = "test-key" });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmChatClient>());
        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmEmbeddingClient>());
        Assert.NotNull(provider.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public void AddLlmProviders_Anthropic_RegistersChatOnly_AndOmitsUnionAndEmbeddingClient_WithoutEmbeddingsProvider()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:Provider"] = "Anthropic", ["Llm:Anthropic:ApiKey"] = "test" });

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddLlmProviders(config));
        Assert.Contains("Anthropic", ex.Message);
        Assert.Contains("Embeddings:Provider", ex.Message);
    }

    [Fact]
    public void AddLlmProviders_AnthropicChatWithNvidiaNimEmbeddings_RegistersBothSeamsIndependently_AndNoUnionClient()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Anthropic",
            ["Llm:Anthropic:ApiKey"] = "anthropic-key",
            ["Embeddings:Provider"] = "NvidiaNim",
            ["Llm:NvidiaNim:ApiKey"] = "nim-key",
        });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<AnthropicLlmClient>(provider.GetRequiredService<ILlmChatClient>());
        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmEmbeddingClient>());
        Assert.Null(provider.GetService<ILlmClient>());
    }

    [Fact]
    public void AddLlmProviders_Ollama_RegistersUnionClient_WithNoApiKeyConfigured()
    {
        // Ollama's server ignores auth entirely -- unlike every other OpenAI-compatible provider,
        // this must not throw when Llm:Ollama:ApiKey is absent (BuildOpenAiCompatibleClient falls
        // back to a placeholder credential for this provider specifically).
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:Provider"] = "Ollama" });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmChatClient>());
        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmEmbeddingClient>());
        Assert.NotNull(provider.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public void AddLlmProviders_NoFallbackProviderConfigured_DoesNotRegisterFallbackKey()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:OpenAI:ApiKey"] = "test-key" });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.FallbackTierKey));
    }

    [Fact]
    public void AddLlmProviders_FallbackProviderConfigured_RegistersKeyedFallbackClient_OfThatProvidersType()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "OpenAI",
            ["Llm:OpenAI:ApiKey"] = "test-key",
            ["Llm:FallbackProvider"] = "Anthropic",
            ["Llm:Anthropic:ApiKey"] = "fallback-key",
        });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmChatClient>()); // primary unchanged
        var fallback = provider.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.FallbackTierKey);
        Assert.IsType<AnthropicLlmClient>(fallback);
    }

    [Fact]
    public void AddLlmProviders_Bedrock_RegistersUnionClient()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:Provider"] = "Bedrock", ["Llm:Bedrock:Region"] = "us-east-1" });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<BedrockLlmClient>(provider.GetRequiredService<ILlmChatClient>());
        Assert.IsType<BedrockLlmClient>(provider.GetRequiredService<ILlmEmbeddingClient>());
        Assert.NotNull(provider.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public void AddLlmProviders_Azure_RegistersUnionClient_ReusingOpenAiLlmClient()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Azure",
            ["Llm:Azure:BaseUrl"] = "https://example.openai.azure.com",
            ["Llm:Azure:ChatModel"] = "gpt-4o-deployment",
            ["Llm:Azure:EmbeddingModel"] = "embedding-deployment",
            ["Llm:Azure:ApiKey"] = "azure-key",
        });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmChatClient>());
        Assert.IsType<OpenAiLlmClient>(provider.GetRequiredService<ILlmEmbeddingClient>());
    }

    [Fact]
    public void AddLlmProviders_RegistersCheapTierClient_AlongsideDefault()
    {
        // Model tiering (gap-closing-solutions.md Phase C, item 3): the keyed "cheap" client must
        // resolve independently of the default ILlmChatClient, whether or not CheapChatModel is set.
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:OpenAI:ApiKey"] = "test-key" });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        var cheap = provider.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey);
        var main = provider.GetRequiredService<ILlmChatClient>();

        Assert.IsType<OpenAiLlmClient>(cheap);
        Assert.NotSame(main, cheap); // distinct client instances, even with no CheapChatModel configured
    }

    [Fact]
    public void AddLlmProviders_Anthropic_CheapTierClient_ReusesConfiguredHttpClientPipeline()
    {
        // Anthropic's cheap-tier client is built manually (not via AddHttpClient<T>) -- this asserts
        // it still resolves via the same named/configured HttpClient rather than a bare default one.
        var services = new ServiceCollection();
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Anthropic",
            ["Llm:Anthropic:ApiKey"] = "test",
            ["Llm:Anthropic:CheapChatModel"] = "claude-haiku",
            ["Embeddings:Provider"] = "NvidiaNim",
            ["Llm:NvidiaNim:ApiKey"] = "nim-key",
        });

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        var cheap = provider.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey);
        Assert.IsType<AnthropicLlmClient>(cheap);
    }

    [Fact]
    public void AddLlmProviders_Throws_ForUnrecognizedProvider()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:Provider"] = "NotAProvider" });

        Assert.Throws<InvalidOperationException>(() => services.AddLlmProviders(config));
    }

    [Fact]
    public void AddLlmProviders_Throws_ForUnrecognizedEmbeddingsProvider()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Anthropic",
            ["Llm:Anthropic:ApiKey"] = "test",
            ["Embeddings:Provider"] = "NotAProvider",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddLlmProviders(config));
        Assert.Contains("NotAProvider", ex.Message);
    }
}
