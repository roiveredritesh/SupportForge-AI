using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// C6 (gap-closing-solutions.md Phase C, item 6): fallback only triggers on the primary's circuit
// being open (a sustained outage its own retry pipeline already gave up on), not on every failure --
// these tests assert that boundary directly, plus the "no fallback mid-stream" safety limit.
public class FallbackLlmChatClientTests
{
    private sealed class FakeLlmChatClient : ILlmChatClient
    {
        public string Name { get; }
        public Exception? ThrowOnComplete { get; set; }
        public string CompleteResult { get; set; } = "";
        public string[] StreamTokens { get; set; } = [];
        public Exception? ThrowDuringStreamAfterTokenIndex { get; set; }
        public int TokenIndexToThrowAt { get; set; } = -1;
        public int LastTotalTokens { get; set; } = 1;
        public bool SupportsVision { get; set; } = true;
        public bool WasCalled { get; private set; }

        public FakeLlmChatClient(string name) => Name = name;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            WasCalled = true;
            if (ThrowOnComplete is not null) throw ThrowOnComplete;
            return Task.FromResult(CompleteResult);
        }

        public Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
        {
            WasCalled = true;
            if (ThrowOnComplete is not null) throw ThrowOnComplete;
            return Task.FromResult(CompleteResult);
        }

        public async IAsyncEnumerable<string> StreamCompleteAsync(
            string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            WasCalled = true;
            for (var i = 0; i < StreamTokens.Length; i++)
            {
                if (i == TokenIndexToThrowAt && ThrowDuringStreamAfterTokenIndex is not null)
                    throw ThrowDuringStreamAfterTokenIndex;
                yield return StreamTokens[i];
                await Task.Yield();
            }
        }
    }

    private static FallbackLlmChatClient MakeSut(FakeLlmChatClient primary, FakeLlmChatClient fallback) =>
        new(primary, fallback, NullLogger<FallbackLlmChatClient>.Instance);

    [Fact]
    public async Task CompleteAsync_PrimarySucceeds_ReturnsPrimaryResult_FallbackNeverCalled()
    {
        var primary = new FakeLlmChatClient("primary") { CompleteResult = "primary answer" };
        var fallback = new FakeLlmChatClient("fallback") { CompleteResult = "fallback answer" };
        var sut = MakeSut(primary, fallback);

        var result = await sut.CompleteAsync("sys", "user");

        Assert.Equal("primary answer", result);
        Assert.False(fallback.WasCalled);
    }

    [Fact]
    public async Task CompleteAsync_PrimaryCircuitOpen_FallsBackToSecondary()
    {
        var primary = new FakeLlmChatClient("primary") { ThrowOnComplete = new BrokenCircuitException("open") };
        var fallback = new FakeLlmChatClient("fallback") { CompleteResult = "fallback answer" };
        var sut = MakeSut(primary, fallback);

        var result = await sut.CompleteAsync("sys", "user");

        Assert.Equal("fallback answer", result);
        Assert.True(fallback.WasCalled);
    }

    [Fact]
    public async Task CompleteAsync_PrimaryThrowsOrdinaryException_PropagatesWithoutFallback()
    {
        // Not BrokenCircuitException -- the primary's own retry pipeline should have handled a
        // transient error already; a non-circuit exception here means something the fallback
        // shouldn't paper over (e.g. a genuinely bad request).
        var primary = new FakeLlmChatClient("primary") { ThrowOnComplete = new InvalidOperationException("bad request") };
        var fallback = new FakeLlmChatClient("fallback") { CompleteResult = "fallback answer" };
        var sut = MakeSut(primary, fallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CompleteAsync("sys", "user"));
        Assert.False(fallback.WasCalled);
    }

    [Fact]
    public async Task AnalyzeImageAsync_PrimaryCircuitOpen_FallsBackToSecondary()
    {
        var primary = new FakeLlmChatClient("primary") { ThrowOnComplete = new BrokenCircuitException("open") };
        var fallback = new FakeLlmChatClient("fallback") { CompleteResult = "fallback vision result" };
        var sut = MakeSut(primary, fallback);

        var result = await sut.AnalyzeImageAsync("base64", "prompt");

        Assert.Equal("fallback vision result", result);
    }

    [Fact]
    public async Task StreamCompleteAsync_PrimarySucceeds_YieldsPrimaryTokens_FallbackNeverCalled()
    {
        var primary = new FakeLlmChatClient("primary") { StreamTokens = ["hello", " ", "world"] };
        var fallback = new FakeLlmChatClient("fallback");
        var sut = MakeSut(primary, fallback);

        var tokens = new List<string>();
        await foreach (var token in sut.StreamCompleteAsync("sys", "user"))
            tokens.Add(token);

        Assert.Equal(new[] { "hello", " ", "world" }, tokens);
        Assert.False(fallback.WasCalled);
    }

    [Fact]
    public async Task StreamCompleteAsync_PrimaryBreaksBeforeFirstToken_FallsBackToSecondary()
    {
        var primary = new FakeLlmChatClient("primary")
        {
            StreamTokens = ["never", "reached"],
            ThrowDuringStreamAfterTokenIndex = new BrokenCircuitException("open"),
            TokenIndexToThrowAt = 0, // breaks before yielding anything
        };
        var fallback = new FakeLlmChatClient("fallback") { StreamTokens = ["fallback", " ", "reply"] };
        var sut = MakeSut(primary, fallback);

        var tokens = new List<string>();
        await foreach (var token in sut.StreamCompleteAsync("sys", "user"))
            tokens.Add(token);

        Assert.Equal(new[] { "fallback", " ", "reply" }, tokens);
    }

    [Fact]
    public async Task StreamCompleteAsync_PrimaryBreaksAfterYieldingTokens_PropagatesException_DoesNotFallBack()
    {
        // Falling back after tokens already reached the caller would duplicate/garble already-sent
        // text -- must propagate instead, same limit OpenAiLlmClient's own streaming design documents.
        var primary = new FakeLlmChatClient("primary")
        {
            StreamTokens = ["partial", "never-reached"],
            ThrowDuringStreamAfterTokenIndex = new BrokenCircuitException("open"),
            TokenIndexToThrowAt = 1, // breaks after the first token was already yielded
        };
        var fallback = new FakeLlmChatClient("fallback") { StreamTokens = ["fallback"] };
        var sut = MakeSut(primary, fallback);

        var tokens = new List<string>();
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await foreach (var token in sut.StreamCompleteAsync("sys", "user"))
                tokens.Add(token);
        });

        Assert.Equal(new[] { "partial" }, tokens);
        Assert.False(fallback.WasCalled);
    }

    [Fact]
    public async Task LastTotalTokensAndSupportsVision_ReflectWhicheverClientActuallyServedTheLastCall()
    {
        var primary = new FakeLlmChatClient("primary") { ThrowOnComplete = new BrokenCircuitException("open") };
        var fallback = new FakeLlmChatClient("fallback") { CompleteResult = "ok", LastTotalTokens = 42, SupportsVision = false };
        var sut = MakeSut(primary, fallback);

        await sut.CompleteAsync("sys", "user");

        Assert.Equal(42, sut.LastTotalTokens);
        Assert.False(sut.SupportsVision);
    }
}
