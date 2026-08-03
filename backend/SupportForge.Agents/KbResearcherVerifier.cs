using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents;

public sealed class KbResearcherVerifier : IAgent
{
    // Vector search always returns topK nearest neighbors with no relevance floor (see the comment
    // below), so this judge is the only check standing between a merely topically-adjacent snippet
    // and an answer that misrepresents it as on-topic. Small/weak judge models default to picking
    // *something* rather than admitting nothing fits, so the prompt states the failure mode and gives
    // a worked example instead of relying on the instruction alone.
    private const string JudgeSystemPrompt = """
        You judge which of several retrieved knowledge-base snippets, if any, actually answers a
        customer's support question -- not merely shares a topic or a keyword with it.

        Picking a snippet that doesn't answer the question is worse than saying none do: the customer
        will be told something false about their situation. When in doubt, answer "none".

        Example: question "what does this repository do?", snippets are a billing FAQ and a password-
        reset guide. Neither describes the repository. Correct answer: none.

        Respond with only the number of the single snippet that directly answers the question, or
        "none" if no snippet does. Respond with nothing else.
        """;

    private readonly ILlmChatClient _llm;
    private readonly ILogger<KbResearcherVerifier> _logger;
    public string Name => "KbResearcherVerifier";

    public KbResearcherVerifier(ILlmChatClient llm, ILogger<KbResearcherVerifier> logger)
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
            if (context.Intent is not ("kb_question" or "code_issue" or "code_question"))
            {
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, intent={Intent} not applicable", Name, sw.ElapsedMilliseconds, context.Intent);
                return context;
            }

            var v = context.KbVerification;

            if (context.KbSnippets.Count == 0)
            {
                Fail(v, "No KB snippets were retrieved.");
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
                    Promote(context.KbSnippets, idx);
                    v.Status = VerificationStatus.Passed;
                }
                else
                {
                    Fail(v, "LLM judge found no retrieved snippet relevant to the query.");
                }
            }

            if (v.Status == VerificationStatus.FailedRetrying)
                _logger.LogWarning("{Agent} retrying: attempt={Attempt} reason={Reason}", Name, v.Attempts, v.Reason);

            // DrafterAgent.BuildUserPrompt dumps context.KbSnippets into the prompt unconditionally --
            // it has no visibility into KbVerification.Status. A final "nothing relevant" verdict has
            // to be enforced here by actually removing the rejected snippets, or the judge's negative
            // verdict only affects the confidence score while the irrelevant content still reaches the
            // model and gets answered from anyway.
            if (v.Status == VerificationStatus.FailedFinal)
                context.KbSnippets.Clear();

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
        var numbered = string.Join("\n", context.KbSnippets.Select((s, i) => $"{i + 1}. {s}"));
        var userPrompt = $"""
            Customer question: {context.Query}
            Retrieved snippets:
            {numbered}
            """;
        var verdict = await _llm.CompleteAsync(JudgeSystemPrompt, userPrompt, ct);
        context.TotalTokensUsed += _llm.LastTotalTokens;

        var trimmed = verdict.Trim();
        if (int.TryParse(trimmed, out var oneBased) && oneBased >= 1 && oneBased <= context.KbSnippets.Count)
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
