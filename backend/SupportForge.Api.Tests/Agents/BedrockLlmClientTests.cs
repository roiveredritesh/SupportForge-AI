using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Options;
using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class BedrockLlmClientTests
{
    private static BedrockOptions BuildOptions() => new()
    {
        ChatModel = "anthropic.claude-3-5-sonnet-test",
        EmbeddingModel = "amazon.titan-embed-text-test",
    };

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task CompleteAsync_ReturnsText_AndTracksTokenUsage_UsingConfiguredChatModel()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        InvokeModelRequest? captured = null;
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = ToStream("""{"content":[{"type":"text","text":"Hello there"}],"usage":{"input_tokens":10,"output_tokens":5}}"""),
            });

        var client = new BedrockLlmClient(bedrock.Object, Options.Create(BuildOptions()));

        var result = await client.CompleteAsync("system prompt", "user prompt", CancellationToken.None);

        Assert.Equal("Hello there", result);
        Assert.Equal(15, client.LastTotalTokens);
        Assert.Equal("anthropic.claude-3-5-sonnet-test", captured!.ModelId);
    }

    [Fact]
    public async Task AnalyzeImageAsync_ReturnsText_AndSendsImageContentBlock()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        InvokeModelRequest? captured = null;
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = ToStream("""{"content":[{"type":"text","text":"a screenshot"}],"usage":{"input_tokens":20,"output_tokens":8}}"""),
            });

        var client = new BedrockLlmClient(bedrock.Object, Options.Create(BuildOptions()));

        var result = await client.AnalyzeImageAsync("YmFzZTY0", "describe this", CancellationToken.None);

        Assert.Equal("a screenshot", result);
        Assert.Equal(28, client.LastTotalTokens);
        var body = new StreamReader(captured!.Body).ReadToEnd();
        Assert.Contains("\"type\":\"image\"", body);
        Assert.Contains("YmFzZTY0", body);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsVector_AndTracksTokenCount_UsingConfiguredEmbeddingModel()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        InvokeModelRequest? captured = null;
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = ToStream("""{"embedding":[0.1,0.2,0.3],"inputTextTokenCount":4}"""),
            });

        var client = new BedrockLlmClient(bedrock.Object, Options.Create(BuildOptions()));

        var vector = await client.EmbedAsync("hello", CancellationToken.None);

        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, vector);
        Assert.Equal(4, client.LastTotalTokens);
        Assert.Equal("amazon.titan-embed-text-test", captured!.ModelId);
    }

    [Fact]
    public void SupportsVision_IsTrue()
    {
        var client = new BedrockLlmClient(new Mock<IAmazonBedrockRuntime>().Object, Options.Create(BuildOptions()));
        Assert.True(client.SupportsVision);
    }

    [Fact]
    public async Task CompleteAsync_SendsTemperatureZero()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        InvokeModelRequest? captured = null;
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = ToStream("""{"content":[{"type":"text","text":"Hello there"}],"usage":{"input_tokens":10,"output_tokens":5}}"""),
            });

        var client = new BedrockLlmClient(bedrock.Object, Options.Create(BuildOptions()));

        await client.CompleteAsync("system prompt", "user prompt", CancellationToken.None);

        var body = new StreamReader(captured!.Body).ReadToEnd();
        Assert.Contains("\"temperature\":0", body);
    }

    [Fact]
    public async Task AnalyzeImageAsync_DoesNotSendTemperature()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        InvokeModelRequest? captured = null;
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new InvokeModelResponse
            {
                Body = ToStream("""{"content":[{"type":"text","text":"a screenshot"}],"usage":{"input_tokens":20,"output_tokens":8}}"""),
            });

        var client = new BedrockLlmClient(bedrock.Object, Options.Create(BuildOptions()));

        await client.AnalyzeImageAsync("YmFzZTY0", "describe this", CancellationToken.None);

        var body = new StreamReader(captured!.Body).ReadToEnd();
        Assert.DoesNotContain("\"temperature\"", body);
    }
}
