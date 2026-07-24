using System.Net;
using Moq;
using Moq.Protected;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class OpenAiLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_ParsesSnakeCaseUsageField_IntoLastTotalTokens()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "choices": [{ "message": { "content": "hello" } }],
                  "usage": { "total_tokens": 42 }
                }
                """)
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var sut = new OpenAiLlmClient(client);

        await sut.CompleteAsync("system", "user");

        Assert.Equal(42, sut.LastTotalTokens);
    }
}
