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
    public void AddLlmProviders_Throws_ForUnrecognizedProvider()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new() { ["Llm:Provider"] = "NotAProvider" });

        Assert.Throws<InvalidOperationException>(() => services.AddLlmProviders(config));
    }
}
