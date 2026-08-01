using Microsoft.Extensions.Configuration;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GraphifyBackendResolverTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Resolve_DerivesOpenAiEnvironment_ForOpenAiCompatibleProvider()
    {
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "NvidiaNim",
            ["Llm:NvidiaNim:BaseUrl"] = "https://integrate.api.nvidia.com/v1/",
            ["Llm:NvidiaNim:ChatModel"] = "meta/llama-3.1-8b-instruct",
            ["Llm:NvidiaNim:ApiKey"] = "nim-key",
        });

        var env = GraphifyBackendResolver.Resolve(config);

        Assert.Equal("https://integrate.api.nvidia.com/v1/", env["OPENAI_BASE_URL"]);
        Assert.Equal("meta/llama-3.1-8b-instruct", env["OPENAI_MODEL"]);
        Assert.Equal("nim-key", env["OPENAI_API_KEY"]);
    }

    [Fact]
    public void Resolve_DerivesAnthropicEnvironment_ForAnthropicProvider()
    {
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Anthropic",
            ["Llm:Anthropic:ChatModel"] = "claude-sonnet-4-20250514",
            ["Llm:Anthropic:ApiKey"] = "anthropic-key",
        });

        var env = GraphifyBackendResolver.Resolve(config);

        Assert.Equal("https://api.anthropic.com", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("claude-sonnet-4-20250514", env["ANTHROPIC_MODEL"]);
        Assert.Equal("anthropic-key", env["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void Resolve_Throws_ForBedrockWithNoGatewayConfigured()
    {
        var config = BuildConfig(new() { ["Llm:Provider"] = "Bedrock" });

        var ex = Assert.Throws<InvalidOperationException>(() => GraphifyBackendResolver.Resolve(config));
        Assert.Contains("Graphify:Gateway:BaseUrl", ex.Message);
    }

    [Fact]
    public void Resolve_Throws_ForAzureWithNoGatewayConfigured()
    {
        var config = BuildConfig(new() { ["Llm:Provider"] = "Azure" });

        var ex = Assert.Throws<InvalidOperationException>(() => GraphifyBackendResolver.Resolve(config));
        Assert.Contains("Graphify:Gateway:BaseUrl", ex.Message);
    }

    [Fact]
    public void Resolve_Throws_WhenGatewayUrlIsNotHttps()
    {
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Bedrock",
            ["Graphify:Gateway:BaseUrl"] = "http://gateway.internal",
            ["Graphify:Gateway:ApiKey"] = "gw-key",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => GraphifyBackendResolver.Resolve(config));
        Assert.Contains("HTTPS", ex.Message);
    }

    [Fact]
    public void Resolve_Throws_WhenGatewayHasNoApiKey()
    {
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Bedrock",
            ["Graphify:Gateway:BaseUrl"] = "https://gateway.internal",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => GraphifyBackendResolver.Resolve(config));
        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void Resolve_DerivesOpenAiEnvironment_ForBedrockWithValidGateway()
    {
        var config = BuildConfig(new()
        {
            ["Llm:Provider"] = "Bedrock",
            ["Llm:Bedrock:ChatModel"] = "anthropic.claude-3-5-sonnet-20241022-v2:0",
            ["Graphify:Gateway:BaseUrl"] = "https://gateway.internal",
            ["Graphify:Gateway:ApiKey"] = "gw-key",
        });

        var env = GraphifyBackendResolver.Resolve(config);

        Assert.Equal("https://gateway.internal", env["OPENAI_BASE_URL"]);
        Assert.Equal("gw-key", env["OPENAI_API_KEY"]);
        Assert.Equal("anthropic.claude-3-5-sonnet-20241022-v2:0", env["OPENAI_MODEL"]);
    }

    [Fact]
    public void Resolve_Throws_ForUnrecognizedProvider()
    {
        var config = BuildConfig(new() { ["Llm:Provider"] = "SomethingElse" });

        Assert.Throws<InvalidOperationException>(() => GraphifyBackendResolver.Resolve(config));
    }
}
