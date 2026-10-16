using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Verification;

namespace SupportForge.Agents;

public sealed class KbResearcherVerifier : IAgent
{
    // U11: the judge sits behind IFindingsJudge. LlmFindingsJudge holds this verifier's KB judge prompt.
    private readonly IFindingsJudge _judge;
    private readonly ILogger<KbResearcherVerifier> _logger;
    public string Name => "KbResearcherVerifier";

    public KbResearcherVerifier(IFindingsJudge judge, ILogger<KbResearcherVerifier> logger)
    {
        _judge = judge;
        _logger = logger;
    }

    // U11: existing call shape. Wraps the client in the LLM judge, which is exactly what this verifier did before.
    public KbResearcherVerifier(ILlmChatClient llm, ILogger<KbResearcherVerifier> logger) : this(new LlmFindingsJudge(llm), logger) { }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", Name, context.ProjectId, context.Intent);
        try
        {
            // U7: shared gate; see IntentRouting.
            if (!IntentRouting.NeedsKb(context))
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
                var judgement = await _judge.JudgeSnippetsAsync(context, FindingsKind.Kb, context.KbSnippets, ct);
                context.AddTokens(Name, judgement.TokensUsed);
                // U11: score-based pruning arrives with the Jev judge (U12); until then BestIndex alone drives the verdict.
                v.Source = judgement.Source;
                v.Scores = judgement.Scores;
                v.Coverage = judgement.Coverage;
                if (judgement.BestIndex is int idx)
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

    private static void Promote(List<string> snippets, int index)
    {
        if (index == 0) return;
        var picked = snippets[index];
        snippets.RemoveAt(index);
        snippets.Insert(0, picked);
    }
}
