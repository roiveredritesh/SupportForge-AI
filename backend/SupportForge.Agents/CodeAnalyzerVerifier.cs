using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerVerifier : IAgent
{
    private const string JudgeSystemPrompt = """
        You judge which of several retrieved source-code snippets, if any, is actually relevant to a
        customer's code-related support question. Respond with only the number of the single most
        relevant snippet, or "none" if none of them are relevant. Respond with nothing else.
        """;

    private readonly ILlmChatClient _llm;
    private readonly ILogger<CodeAnalyzerVerifier> _logger;
    public string Name => "CodeAnalyzerVerifier";

    public CodeAnalyzerVerifier(ILlmChatClient llm, ILogger<CodeAnalyzerVerifier> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", Name, context.ProjectId, context.Intent);
        try
        {
            if (context.Intent is not ("code_issue" or "code_question"))
            {
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, intent={Intent} not applicable", Name, sw.ElapsedMilliseconds, context.Intent);
                return context;
            }

            var v = context.CodeVerification;

            if (context.CodeSnippets.Count == 0)
            {
                Fail(v, "No code snippets were retrieved for a code-related query.");
            }
            else
            {
                // Vector search always returns topK nearest neighbors with no relevance floor, so a
                // non-empty result set doesn't mean the content is on-topic, and the closest (#1) match
                // isn't guaranteed to be the most relevant one either -- judge every retrieved snippet
                // together in one call and promote whichever one the judge picks, instead of discarding
                // the whole branch when only #1 happens to miss.
                var matchIndex = await JudgeAsync(context, ct);
                if (matchIndex is int idx)
                {
                    Promote(context.CodeSnippets, idx);
                    v.Status = VerificationStatus.Passed;
                }
                else
                {
                    Fail(v, "LLM judge found no retrieved code snippet relevant to the query.");
                }
            }

            if (v.Status == VerificationStatus.FailedRetrying)
                _logger.LogWarning("{Agent} retrying: attempt={Attempt} reason={Reason}", Name, v.Attempts, v.Reason);

            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: status={Status} reason={Reason}", Name, sw.ElapsedMilliseconds, v.Status, v.Reason);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static void Fail(VerificationResult v, string reason)
    {
        v.Status = v.Attempts < 2 ? VerificationStatus.FailedRetrying : VerificationStatus.FailedFinal;
        v.Reason = reason;
    }

    // Returns the 0-based index of the snippet the judge picked, or null when none are relevant.
    private async Task<int?> JudgeAsync(AgentContext context, CancellationToken ct)
    {
        var numbered = string.Join("\n", context.CodeSnippets.Select((s, i) => $"{i + 1}. {s}"));
        var userPrompt = $"""
            Customer question: {context.Query}
            Retrieved code snippets:
            {numbered}
            """;
        var verdict = await _llm.CompleteAsync(JudgeSystemPrompt, userPrompt, ct);
        context.TotalTokensUsed += _llm.LastTotalTokens;

        var trimmed = verdict.Trim();
        if (int.TryParse(trimmed, out var oneBased) && oneBased >= 1 && oneBased <= context.CodeSnippets.Count)
            return oneBased - 1;
        return null;
    }

    private static void Promote(List<string> snippets, int index)
    {
        if (index == 0) return;
        var picked = snippets[index];
        snippets.RemoveAt(index);
        snippets.Insert(0, picked);
    }
}
