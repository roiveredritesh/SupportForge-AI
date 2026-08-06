using System.Net;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Polly.CircuitBreaker;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

/// <summary>
/// Covers U2 (LLM client resilience, KTD2): retry-with-backoff and circuit-breaker behavior for
/// each of the three LLM clients, using each client's own idiomatic mechanism -- Polly attached to
/// the HttpClient pipeline for Anthropic, an in-process Polly pipeline wrapping the call site for
/// OpenAI (no HttpClient seam), and the AWS SDK's own retry config for Bedrock.
/// </summary>
public class LlmClientResilienceTests
{
    // ---- Anthropic: Polly attached via AddResilienceHandler on AddHttpClient<AnthropicLlmClient> ----

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int CallCount { get; private set; }

        public SequencedHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(next());
        }
    }

    private static HttpResponseMessage AnthropicOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}"""),
    };

    private static HttpResponseMessage AnthropicServiceUnavailable() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("""{"error":{"message":"overloaded"}}"""),
    };

    private static AnthropicLlmClient BuildAnthropicClient(SequencedHandler handler)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "Anthropic",
                ["Llm:Anthropic:ApiKey"] = "test",
                ["Embeddings:Provider"] = "OpenAI",
                ["Llm:OpenAI:ApiKey"] = "unused",
            })
            .Build();

        services.AddLlmProviders(config);
        // Same typed-client registration key as AddLlmProviders' internal AddHttpClient<AnthropicLlmClient>
        // call -- HttpClientFactory merges configuration actions across calls for the same client type,
        // so this swaps in the fake transport without needing AddLlmProviders to expose the builder.
        services.AddHttpClient<AnthropicLlmClient>().ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<AnthropicLlmClient>();
    }

    [Fact]
    public async Task Anthropic_HappyPath_NoRetryOverhead()
    {
        var handler = new SequencedHandler(AnthropicOk);
        var client = BuildAnthropicClient(handler);

        var result = await client.CompleteAsync("s", "u");

        Assert.Equal("ok", result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Anthropic_TransientFailure_SucceedsOnRetry()
    {
        var handler = new SequencedHandler(AnthropicServiceUnavailable, AnthropicOk);
        var client = BuildAnthropicClient(handler);

        var result = await client.CompleteAsync("s", "u");

        Assert.Equal("ok", result);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Anthropic_SustainedFailure_SurfacesClearException()
    {
        var handler = new SequencedHandler(AnthropicServiceUnavailable);
        var client = BuildAnthropicClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u"));

        Assert.Contains("overloaded", ex.Message);
        // MaxRetryAttempts = 3 -> 1 initial + 3 retries = 4 attempts total.
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Anthropic_CircuitBreaker_OpensAfterSustainedFailure_AndFailsFast()
    {
        var handler = new SequencedHandler(AnthropicServiceUnavailable);
        var client = BuildAnthropicClient(handler);

        // First call exhausts the retry budget (4 attempts), which also trips MinimumThroughput on
        // the inner circuit breaker (opens on the 4th recorded failure).
        await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteAsync("s", "u"));
        var callsAfterFirstRequest = handler.CallCount;

        // Second call should fail fast: the breaker is open, so the transport is never hit again.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(callsAfterFirstRequest, handler.CallCount);
    }

    // ---- OpenAI: in-process Polly pipeline wrapping the call site (no HttpClient seam) ----

    [Fact]
    public async Task OpenAi_HappyPath_NoRetryOverhead()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model");

        var result = await sut.CompleteAsync("system", "user");

        Assert.Equal("hello", result);
        chatClient.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenAi_TransientFailure_SucceedsOnRetry()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .SetupSequence(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered")));

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model");

        var result = await sut.CompleteAsync("system", "user");

        Assert.Equal("recovered", result);
        chatClient.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OpenAi_SustainedFailure_SurfacesClearException()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sut.CompleteAsync("system", "user"));

        Assert.Equal("provider down", ex.Message);
        // MaxRetryAttempts = 3 -> 1 initial + 3 retries = 4 attempts total.
        chatClient.Verify(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task OpenAi_CircuitBreaker_OpensAfterSustainedFailure_AndFailsFast()
    {
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var sut = new OpenAiLlmClient(chatClient.Object, null!, "unused-model");

        // First call exhausts the retry budget, which also trips the circuit breaker.
        await Assert.ThrowsAnyAsync<Exception>(() => sut.CompleteAsync("system", "user"));
        var callsAfterFirstRequest = chatClient.Invocations.Count;

        // Second call should fail fast without invoking the underlying chat client again.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => sut.CompleteAsync("system", "user"));

        Assert.Equal(callsAfterFirstRequest, chatClient.Invocations.Count);
    }

    // ---- Bedrock: Polly pipeline wrapping the call site (C6 -- gap-closing-solutions.md Phase C item 6) ----
    // Same shape as OpenAI's tests above, just mocking IAmazonBedrockRuntime.InvokeModelAsync directly
    // instead of IChatClient.

    private static InvokeModelResponse BedrockTextResponse(string text) => new()
    {
        Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"content":[{"type":"text","text":"REPLACE_ME"}],"usage":{"input_tokens":1,"output_tokens":1}}""".Replace("REPLACE_ME", text))),
    };

    private static BedrockLlmClient BuildBedrockClient(Mock<IAmazonBedrockRuntime> bedrock) =>
        new(bedrock.Object, Microsoft.Extensions.Options.Options.Create(new BedrockOptions()));

    [Fact]
    public async Task Bedrock_HappyPath_NoRetryOverhead()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BedrockTextResponse("hello"));

        var sut = BuildBedrockClient(bedrock);
        var result = await sut.CompleteAsync("system", "user");

        Assert.Equal("hello", result);
        bedrock.Verify(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Bedrock_ThrottlingException_SucceedsOnRetry()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        bedrock.SetupSequence(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.BedrockRuntime.Model.ThrottlingException("rate limited"))
            .ReturnsAsync(BedrockTextResponse("recovered"));

        var sut = BuildBedrockClient(bedrock);
        var result = await sut.CompleteAsync("system", "user");

        Assert.Equal("recovered", result);
        bedrock.Verify(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Bedrock_NonTransientException_PropagatesWithoutRetry()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.BedrockRuntime.Model.ValidationException("bad request"));

        var sut = BuildBedrockClient(bedrock);

        await Assert.ThrowsAsync<Amazon.BedrockRuntime.Model.ValidationException>(() => sut.CompleteAsync("system", "user"));
        bedrock.Verify(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Bedrock_CircuitBreaker_OpensAfterSustainedFailure_AndFailsFast()
    {
        var bedrock = new Mock<IAmazonBedrockRuntime>();
        bedrock.Setup(b => b.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.BedrockRuntime.Model.ServiceUnavailableException("overloaded"));

        var sut = BuildBedrockClient(bedrock);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.CompleteAsync("system", "user"));
        var callsAfterFirstRequest = bedrock.Invocations.Count;

        await Assert.ThrowsAsync<BrokenCircuitException>(() => sut.CompleteAsync("system", "user"));

        Assert.Equal(callsAfterFirstRequest, bedrock.Invocations.Count);
    }

    // ---- Bedrock: AWS SDK's own retry config (RetryMode/MaxErrorRetry), not Polly ----
    // BedrockLlmClient talks to IAmazonBedrockRuntime, an interface the AWS SDK's retry pipeline
    // lives behind (not visible through a mocked IAmazonBedrockRuntime, since retries happen inside
    // the concrete client's internal HTTP handler chain). What's testable at this layer is that the
    // DI registration actually configures the SDK's retry behavior -- see KTD2.

    [Fact]
    public void Bedrock_DiRegistration_ConfiguresSdkRetryMode()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "Bedrock",
                ["Llm:Bedrock:Region"] = "us-east-1",
            })
            .Build();

        services.AddLlmProviders(config);
        using var provider = services.BuildServiceProvider();

        var bedrockClient = Assert.IsType<AmazonBedrockRuntimeClient>(provider.GetRequiredService<IAmazonBedrockRuntime>());

        Assert.Equal(RequestRetryMode.Standard, bedrockClient.Config.RetryMode);
        // C6: dialed down from 3 to 1 now that BedrockLlmClient wraps calls in its own Polly
        // retry+circuit-breaker pipeline too -- see LlmServiceCollectionExtensions.EnsureBedrockRuntimeRegistered.
        Assert.Equal(1, bedrockClient.Config.MaxErrorRetry);
    }
}
