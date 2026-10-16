using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Verification;

namespace SupportForge.Agents;

public sealed class KbResearcherVerifier : IAgent
{
    // U11: the judge sits behind IFindingsJudge. LlmFindingsJudge holds this verifier's KB judge prompt.
    private readonly IFindingsJudge _judge;
    // U12: Verification:MinRelevance and PartialCoverageBelow drive the score policy below.
    private readonly VerificationOptions _options;
    private readonly ILogger<KbResearcherVerifier> _logger;
    public string Name => "KbResearcherVerifier";

    public KbResearcherVerifier(IFindingsJudge judge, VerificationOptions options, ILogger<KbResearcherVerifier> logger)
    {
        _judge = judge;
        _options = options;
        _logger = logger;
    }

    public KbResearcherVerifier(IFindingsJudge judge, ILogger<KbResearcherVerifier> logger) : this(judge, new VerificationOptions(), logger) { }

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

            // U18: reset on every attempt, so a retry never inherits the previous attempt's count.
            v.InjectionDropped = 0;

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
                v.Source = judgement.Source;
                v.Coverage = judgement.Coverage;
                // U13: coverage is per attempt, so a retry never inherits the previous attempt's flag.
                v.CoverageIsPartial = judgement.Coverage is double coverage && coverage < _options.PartialCoverageBelow;

                // U18 (R26): a snippet that reaches IndirectThreshold is dropped whatever its relevance. This runs before the
                // relevance policy, so a flagged snippet can't carry the verdict or the top score. Judges that don't screen
                // leave InjectionScores null, and then nothing here changes.
                var scores = judgement.Scores;
                var bestIndex = judgement.BestIndex;
                var flagged = IndirectInjectionScreen.FlaggedIndexes(judgement.InjectionScores, context.KbSnippets.Count, _options.IndirectThreshold);
                if (flagged.Count > 0)
                {
                    IndirectInjectionScreen.Remove(context.KbSnippets, flagged);
                    scores = IndirectInjectionScreen.RemoveScores(scores, flagged);
                    bestIndex = IndirectInjectionScreen.RemapIndex(bestIndex, flagged);
                    v.InjectionDropped = flagged.Count;

                    // U18: indexes and counts only, never snippet text.
                    _logger.LogWarning("IndirectInjectionDropped: verifier={Verifier} project={ProjectId} indexes={Indexes}", Name, context.ProjectId, IndirectInjectionScreen.Join(flagged));
                    Activity.Current?.SetTag("verify.injection_dropped", flagged.Count);
                }

                v.Scores = scores;

                if (context.KbSnippets.Count == 0)
                {
                    Fail(v, "Every retrieved snippet was flagged as containing instructions addressed to an AI assistant.");
                }
                else if (scores is not null)
                {
                    // U12 (R15): per-snippet scores decide which snippets reach the Drafter.
                    ApplyScores(v, context.KbSnippets, scores);
                }
                else if (bestIndex is int idx)
                {
                    // U11: the LLM judge reports only BestIndex, so the promote-and-keep path stays byte-for-byte as before.
                    Promote(context.KbSnippets, idx);
                    v.Status = VerificationStatus.Passed;
                }
                else
                {
                    Fail(v, "LLM judge found no retrieved snippet relevant to the query.");
                }

                // U14: span tags carry labels and scores only, never snippet text.
                var activity = Activity.Current;
                activity?.SetTag("verify.source", v.Source);
                if (v.Scores is { Count: > 0 } tagScores) activity?.SetTag("verify.top_score", tagScores.Max());
                if (v.Coverage is double tagCoverage) activity?.SetTag("verify.coverage", tagCoverage);
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

    // U12 (R15): keep every snippet scoring at or above MinRelevance, best first (ties keep retrieval order), and drop the rest.
    // Scores beyond snippets.Count are ignored, so a judge that returns extra entries cannot keep a snippet that does not exist.
    private void ApplyScores(VerificationResult v, List<string> snippets, IReadOnlyList<double> scores)
    {
        var kept = Enumerable.Range(0, Math.Min(scores.Count, snippets.Count))
            .Where(i => scores[i] >= _options.MinRelevance)
            .OrderByDescending(i => scores[i])
            .ToList();

        // U14: counts only.
        Activity.Current?.SetTag("verify.kept", kept.Count);
        Activity.Current?.SetTag("verify.dropped", snippets.Count - kept.Count);

        if (kept.Count == 0)
        {
            Fail(v, "Judge scored no retrieved snippet at or above Verification:MinRelevance.");
            return;
        }

        var keptSnippets = kept.Select(i => snippets[i]).ToList();
        snippets.Clear();
        snippets.AddRange(keptSnippets);
        v.Status = VerificationStatus.Passed;
    }
}
