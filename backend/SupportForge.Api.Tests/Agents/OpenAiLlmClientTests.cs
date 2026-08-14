using Microsoft.Extensions.AI;
using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class OpenAiLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_ReadsUsage_IntoLastTotalTokens()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello"))
            {
                Usage = new UsageDetails { TotalTokenCount = 42 },
            });

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model");

        var result = await sut.CompleteAsync("system", "user");

        Assert.Equal("hello", result);
        Assert.Equal(42, sut.LastTotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_PassesTemperatureZero_AndDefaultSeed_WhenNoneConfigured()
    {
        var chatClient = new Mock<IChatClient>();
        ChatOptions? captured = null;
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model");

        await sut.CompleteAsync("system", "user");

        Assert.NotNull(captured);
        Assert.Equal(0f, captured!.Temperature);
        Assert.Equal(42, captured.Seed);
    }

    [Fact]
    public async Task CompleteAsync_PassesConfiguredSeed_WhenProvided()
    {
        var chatClient = new Mock<IChatClient>();
        ChatOptions? captured = null;
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, options, _) => captured = options)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model", seed: 12345);

        await sut.CompleteAsync("system", "user");

        Assert.Equal(12345, captured!.Seed);
    }
}
