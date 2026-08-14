using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class AnthropicLlmClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond(request);
        }
    }

    private static AnthropicLlmClient CreateClient(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new AnthropicOptions { ApiKey = "test-key", ChatModel = "claude-test" }));

    [Fact]
    public async Task CompleteAsync_ReturnsText_AndTracksTokenUsage()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"content":[{"type":"text","text":"Hello there"}],"usage":{"input_tokens":10,"output_tokens":5}}""",
                Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(handler);

        var result = await client.CompleteAsync("system prompt", "user prompt", CancellationToken.None);

        Assert.Equal("Hello there", result);
        Assert.Equal(15, client.LastTotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_SendsAuthAndVersionHeaders_AndSystemPromptAsTopLevelField()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}"""),
        });
        var client = CreateClient(handler);

        await client.CompleteAsync("be terse", "hi", CancellationToken.None);

        Assert.Equal("test-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("\"system\":\"be terse\"", handler.LastRequestBody);
        Assert.DoesNotContain("\"role\":\"system\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task AnalyzeImageAsync_SendsImageContentBlock()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"a screenshot"}],"usage":{"input_tokens":20,"output_tokens":8}}"""),
        });
        var client = CreateClient(handler);

        var result = await client.AnalyzeImageAsync("YmFzZTY0", "describe this", CancellationToken.None);

        Assert.Equal("a screenshot", result);
        Assert.Equal(28, client.LastTotalTokens);
        Assert.Contains("\"type\":\"image\"", handler.LastRequestBody);
        Assert.Contains("YmFzZTY0", handler.LastRequestBody);
    }

    [Fact]
    public async Task StreamCompleteAsync_YieldsDeltasInOrder_AndTracksFinalTokenUsage()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":12}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        var client = CreateClient(handler);

        var chunks = new List<string>();
        await foreach (var chunk in client.StreamCompleteAsync("system", "user", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(new[] { "Hello", " world" }, chunks);
        Assert.Equal(19, client.LastTotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsHttpRequestException_OnErrorResponse()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"invalid x-api-key"}}"""),
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Contains("invalid x-api-key", ex.Message);
    }

    [Fact]
    public void SupportsVision_IsTrue()
    {
        var client = CreateClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(client.SupportsVision);
    }

    [Fact]
    public async Task CompleteAsync_SendsTemperatureZero()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}"""),
        });
        var client = CreateClient(handler);

        await client.CompleteAsync("system prompt", "user prompt", CancellationToken.None);

        Assert.Contains("\"temperature\":0", handler.LastRequestBody);
    }

    [Fact]
    public async Task AnalyzeImageAsync_DoesNotSendTemperature()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"a screenshot"}],"usage":{"input_tokens":20,"output_tokens":8}}"""),
        });
        var client = CreateClient(handler);

        await client.AnalyzeImageAsync("YmFzZTY0", "describe this", CancellationToken.None);

        Assert.DoesNotContain("\"temperature\"", handler.LastRequestBody);
    }
}
