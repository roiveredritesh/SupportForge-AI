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
}
