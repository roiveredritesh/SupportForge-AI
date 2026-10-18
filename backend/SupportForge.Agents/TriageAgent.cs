using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Triage;

namespace SupportForge.Agents;

// U2/U6 (JEV intent classification plan): TriageAgent picks the intent for the turn. Classification sits behind
// IIntentClassifier, and Triage:Classifier selects the mode (R7):
//   Llm    - the LLM classifier only. Behaviour is unchanged from before Jev existed.
//   Jev    - the Jev classifier under its own budget. Any failure, timeout or late answer falls back to the LLM in the same
//            request (R6). Only caller cancellation propagates.
//   Shadow - the LLM classifier is acted on, and the Jev answer is logged next to it (TriageShadow). Jev never throws.
public sealed class TriageAgent : IAgent
{
    private const string FallbackSource = "llm-fallback";

    private readonly IIntentClassifier _llm;
    private readonly IIntentClassifier? _jev;
    private readonly TriageOptions _options;
    private readonly ILogger<TriageAgent> _logger;
    public string Name => "Triage";

    public TriageAgent(IIntentClassifier llm, IIntentClassifier? jev, TriageOptions options, ILogger<TriageAgent> logger)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _jev = jev;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (options.Classifier != TriageClassifierMode.Llm && jev is null)
        {
            throw new ArgumentException(
                $"Triage:Classifier is {options.Classifier} but no Jev classifier was supplied.", nameof(jev));
        }
    }

    // Convenience constructor kept so every existing call site keeps compiling unchanged (LLM mode, default options).
    public TriageAgent(IIntentClassifier classifier, ILogger<TriageAgent> logger) : this(classifier, null, new TriageOptions(), logger) { }

    public TriageAgent(ILlmChatClient llm, ILogger<TriageAgent> logger) : this(new LlmIntentClassifier(llm), logger) { }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} queryLength={QueryLength}", Name, context.ProjectId, context.Query.Length);
        try
        {
            var classification = await ClassifyAsync(context, ct);
            var decision = classification.Decision;
            var hasScreenshot = !string.IsNullOrEmpty(context.ScreenshotBase64);

            // Only Jev decisions go through TriageDecisionPolicy (Noul overrides, then unclear on low confidence or a small
            // margin). An LLM decision keeps the top-label mapping. The policy would pass a single-label LLM answer through
            // unchanged, so skipping it keeps the LLM path exactly as it was (R1), and Llm mode never reaches policy code.
            var policy = classification.FromJev
                ? TriageDecisionPolicy.Decide(decision, hasScreenshot, _options)
                : LlmRule(decision);

            context.Intent = policy.Intent;
            context.IntentRule = policy.AppliedRule;
            context.IntentConfidence = decision.Confidence;
            context.IntentScores = decision.Scores;
            context.IntentSignals = decision.Signals;
            context.IntentSource = decision.Source;
            context.IntentConfidenceBand = ConfidenceBandFor(context.Intent, decision);
            // Secondary routing (R8) only widens retrieval for Jev intents. LLM intents never enable it.
            context.SecondaryIntentEnabled = _options.SecondaryIntentRouting && context.IntentSource == "jev";
            context.SecondaryMinProbability = _options.SecondaryMinProbability;
            context.AddTokens(Name, decision.TokensUsed);

            if (classification.Shadow is { } shadow)
            {
                LogShadow(context, shadow, hasScreenshot);
            }

            // R10: the span is opened by CoordinatorPipeline's AgentExecutor. Tags carry labels and scores only, never text.
            var activity = Activity.Current;
            activity?.SetTag("triage.intent", context.Intent);
            activity?.SetTag("triage.confidence", decision.Confidence);
            activity?.SetTag("triage.source", context.IntentSource);
            activity?.SetTag("triage.rule", context.IntentRule);
            activity?.SetTag("triage.runner_up", context.RunnerUpIntent?.Label);

            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: intent={Intent}", Name, sw.ElapsedMilliseconds, context.Intent);
            _logger.LogInformation("DIAG triage rule={Rule} confidence={Confidence:F2} scores={Scores}", context.IntentRule, decision.Confidence,
                string.Join(",", decision.Scores.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}:{kv.Value:F2}")));
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    // Picks the acting decision for the configured mode. Llm mode never touches the Jev classifier. In Jev mode a precomputed
    // decision (U17) is acted on as if Jev had just answered; its tokens are added under Triage by RunAsync, as for any Jev answer.
    private async Task<Classification> ClassifyAsync(AgentContext context, CancellationToken ct)
    {
        switch (_options.Classifier)
        {
            case TriageClassifierMode.Jev:
                // U17 / KTD 13: the guard's combined request already carried the triage answer, so no second Jev call is made.
                if (context.PrecomputedIntent is { } precomputed)
                {
                    _logger.LogInformation("TriagePrecomputed: using the guard's combined Jev answer. project={ProjectId}", context.ProjectId);
                    return new Classification(precomputed, FromJev: true, Shadow: null);
                }

                return await ClassifyWithJevAsync(context, ct);

            case TriageClassifierMode.Shadow:
                return await ClassifyShadowAsync(context, ct);

            default:
                var llm = await _llm.ClassifyAsync(context, TriageLabels.All, ct);
                return new Classification(llm, FromJev: false, Shadow: null);
        }
    }

    // Jev mode (R6). Jev runs under JevTimeout, and anything other than caller cancellation falls back to the LLM classifier.
    private async Task<Classification> ClassifyWithJevAsync(AgentContext context, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var jevCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            jevCts.CancelAfter(_options.JevTimeout);
            var answer = await WaitForJevAsync(RunJevAsync(context, clock, jevCts.Token), clock, jevCts);
            return new Classification(answer.Decision, FromJev: true, Shadow: null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Only the exception type and timings are logged. The message and the query text never reach the logger.
            _logger.LogWarning(
                "TriageJevFallback: exceptionType={ExceptionType} project={ProjectId} elapsedMs={ElapsedMs}. Classifying with the LLM instead.",
                ex.GetType().Name, context.ProjectId, clock.ElapsedMilliseconds);
            var fallback = await _llm.ClassifyAsync(context, TriageLabels.All, ct);
            // The Jev tokens are not counted here: a failed Jev call has no usage to report.
            return new Classification(fallback with { Source = FallbackSource }, FromJev: false, Shadow: null);
        }
    }

    // Shadow mode (R7). Both classifiers start together. The LLM answer is the one acted on, and Jev is waited for only within
    // its budget. A Jev failure is recorded, never thrown, so Shadow adds no latency beyond the LLM path.
    private async Task<Classification> ClassifyShadowAsync(AgentContext context, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var jevCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jevCts.CancelAfter(_options.JevTimeout);
        var jevTask = RunJevAsync(context, clock, jevCts.Token);

        IntentDecision llm;
        try
        {
            llm = await _llm.ClassifyAsync(context, TriageLabels.All, ct);
        }
        catch (Exception)
        {
            // The LLM is the acting path, so its failure fails the request as it does in Llm mode. Stop the Jev call too.
            jevCts.Cancel();
            throw;
        }

        JevAnswer? jev = null;
        try
        {
            jev = await WaitForJevAsync(jevTask, clock, jevCts);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Jev failed, ran out of budget or answered late. The LLM result stands and TriageShadow records jevFailed.
        }

        return new Classification(llm, FromJev: false, Shadow: new ShadowReading(jev, clock.Elapsed.TotalMilliseconds));
    }

    // Starts the Jev classifier. The answer is stamped with its arrival time, so a result that lands after JevTimeout counts as
    // failed even if the caller reads it later (for example, after the LLM call in Shadow).
    private async Task<JevAnswer> RunJevAsync(AgentContext context, Stopwatch clock, CancellationToken token)
    {
        var jev = _jev ?? throw new InvalidOperationException("Triage:Classifier needs a Jev classifier, and none was supplied.");
        var decision = await jev.ClassifyAsync(context, TriageLabels.All, token);
        return new JevAnswer(decision, clock.Elapsed);
    }

    // Waits for Jev within JevTimeout, measured from `clock`. The wait is bounded even if the Jev classifier ignores its token.
    // Throws when Jev fails, runs out of budget, or answers after the budget.
    private async Task<JevAnswer> WaitForJevAsync(Task<JevAnswer> jevTask, Stopwatch clock, CancellationTokenSource jevCts)
    {
        var remaining = _options.JevTimeout - clock.Elapsed;
        var budget = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        if (await Task.WhenAny(jevTask, budget) != jevTask)
        {
            jevCts.Cancel();
            throw new TimeoutException("Jev did not answer within TriageOptions.JevTimeout.");
        }

        var answer = await jevTask;
        if (answer.Elapsed > _options.JevTimeout)
        {
            throw new TimeoutException("Jev answered after TriageOptions.JevTimeout.");
        }

        return answer;
    }

    // R10 / U8: the Drafter's confidence hint. Only Jev decisions carry a band. An LLM decision has no band, so LLM-mode
    // prompts stay unchanged.
    private string? ConfidenceBandFor(string intent, IntentDecision decision)
    {
        if (decision.Source != "jev" || intent == TriageLabels.Unclear)
        {
            return null;
        }

        return decision.Confidence >= _options.HighConfidence ? "high" : "medium";
    }

    // The LLM path's rule: the highest-scoring label, and anything that can't be mapped becomes "unclear".
    private static TriagePolicyResult LlmRule(IntentDecision decision)
    {
        var top = TopLabel(decision.Scores);
        return top is not null && TriageLabels.IsKnown(top)
            ? new TriagePolicyResult(top, "top")
            : new TriagePolicyResult(TriageLabels.Unclear, "unknown-label");
    }

    // Logs the Jev answer beside the LLM answer, with labels, confidence, rule and timings only. Its tokens are recorded
    // under "Triage.Shadow" when Jev answered, and are not counted as unknown.
    private void LogShadow(AgentContext context, ShadowReading shadow, bool hasScreenshot)
    {
        var jev = shadow.Jev;
        string? jevIntent = null;
        string? jevRule = null;
        double? jevConfidence = null;
        if (jev is not null)
        {
            var jevPolicy = TriageDecisionPolicy.Decide(jev.Decision, hasScreenshot, _options);
            jevIntent = jevPolicy.Intent;
            jevRule = jevPolicy.AppliedRule;
            jevConfidence = jev.Decision.Confidence;
            context.AddTokens($"{Name}.Shadow", jev.Decision.TokensUsed);
        }

        _logger.LogInformation(
            "TriageShadow: llmIntent={LlmIntent} jevIntent={JevIntent} jevConfidence={JevConfidence} jevRule={JevRule} agree={Agree} jevLatencyMs={JevLatencyMs} jevFailed={JevFailed}",
            context.Intent,
            jevIntent,
            jevConfidence,
            jevRule,
            jev is not null && jevIntent == context.Intent,
            (long)shadow.JevLatencyMs,
            jev is null);
    }

    // Highest-scoring label; ties go to TriageLabels.All order. Empty (or all-NaN) scores return null.
    // A top label outside TriageLabels is returned as-is and mapped to "unclear" by the caller.
    private static string? TopLabel(IReadOnlyDictionary<string, double> scores)
    {
        string? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var (label, score) in scores)
        {
            if (double.IsNaN(score))
            {
                continue;
            }

            if (best is null || score > bestScore || (score == bestScore && TriageLabels.CompareTieBreak(label, best) < 0))
            {
                best = label;
                bestScore = score;
            }
        }

        return best;
    }

    // Which path produced the acting decision. FromJev routes it through TriageDecisionPolicy.
    private sealed record Classification(IntentDecision Decision, bool FromJev, ShadowReading? Shadow);

    private sealed record JevAnswer(IntentDecision Decision, TimeSpan Elapsed);

    // Shadow's Jev side: null when Jev failed, ran out of budget or answered late.
    private sealed record ShadowReading(JevAnswer? Jev, double JevLatencyMs);
}
