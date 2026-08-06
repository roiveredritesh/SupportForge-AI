using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace SupportForge.Agents;

/// <summary>
/// C6 (gap-closing-solutions.md Phase C, item 6): wraps a primary ILlmChatClient with a fallback,
/// switching to the fallback specifically when the primary's own resilience pipeline has its circuit
/// open (BrokenCircuitException) -- i.e. a sustained outage, not any single transient error, since
/// the primary's own retry+backoff already absorbs those. Only wired up for DrafterAgent (the
/// highest-value call) per the design doc's scope, not every agent at once.
/// </summary>
public sealed class FallbackLlmChatClient : ILlmChatClient
{
    private readonly ILlmChatClient _primary;
    private readonly ILlmChatClient _fallback;
    private readonly ILogger<FallbackLlmChatClient> _logger;
    private ILlmChatClient _lastUsed;

    public FallbackLlmChatClient(ILlmChatClient primary, ILlmChatClient fallback, ILogger<FallbackLlmChatClient> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
        _lastUsed = primary;
    }

    public int LastTotalTokens => _lastUsed.LastTotalTokens;
    public bool SupportsVision => _lastUsed.SupportsVision;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        try
        {
            var result = await _primary.CompleteAsync(systemPrompt, userPrompt, ct);
            _lastUsed = _primary;
            return result;
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "Primary LLM provider's circuit is open; falling back to secondary provider");
            _lastUsed = _fallback;
            return await _fallback.CompleteAsync(systemPrompt, userPrompt, ct);
        }
    }

    public async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
    {
        try
        {
            var result = await _primary.AnalyzeImageAsync(base64Image, prompt, ct);
            _lastUsed = _primary;
            return result;
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "Primary LLM provider's circuit is open; falling back to secondary provider");
            _lastUsed = _fallback;
            return await _fallback.AnalyzeImageAsync(base64Image, prompt, ct);
        }
    }

    // Streaming can't cleanly retry mid-stream -- once a chunk has been yielded to the caller it may
    // already be rendering client-side, so falling back is only safe if the primary breaks before
    // yielding anything. A failure after that point propagates unchanged (same limitation
    // OpenAiLlmClient's own "streaming isn't retried" comment documents).
    public async IAsyncEnumerable<string> StreamCompleteAsync(
        string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var yieldedAny = false;
        var primaryBroken = false;
        var enumerator = _primary.StreamCompleteAsync(systemPrompt, userPrompt, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (BrokenCircuitException ex) when (!yieldedAny)
                {
                    _logger.LogWarning(ex, "Primary LLM provider's circuit is open before any tokens streamed; falling back to secondary provider");
                    primaryBroken = true;
                    break;
                }

                if (!hasNext)
                {
                    _lastUsed = _primary;
                    yield break;
                }

                yieldedAny = true;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (primaryBroken)
        {
            _lastUsed = _fallback;
            await foreach (var chunk in _fallback.StreamCompleteAsync(systemPrompt, userPrompt, ct))
                yield return chunk;
        }
    }
}
