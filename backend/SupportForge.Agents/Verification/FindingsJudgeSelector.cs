using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents.Verification;

// U12 (JEV intent classification plan, R20 / R21): chooses which findings judge acts, per Verification:Judge. It follows TriageAgent's
// classifier selection (U6), so Jev can never fail a request:
//   Llm    - the LLM judge only. Behaviour is unchanged from before Jev existed.
//   Jev    - the Jev judge under Verification:JevTimeout. Any failure, timeout or late answer falls back to the LLM judge in the same
//            call (Source "llm-fallback"). Only caller cancellation propagates.
//   Shadow - the LLM judge is acted on, and the Jev answer is logged next to it (VerifierShadow). Jev never throws.
public sealed class FindingsJudgeSelector : IFindingsJudge
{
    private const string FallbackSource = "llm-fallback";
    private const string ShadowTokensAgent = "Verification.Shadow";

    // The legacy vision gate: findings of this length or more auto-passed before the Jev judge existed (R20 keeps that rule for fallback).
    private const int LegacyVisionAutoPassLength = 20;

    private readonly IFindingsJudge _llm;
    private readonly IFindingsJudge? _jev;
    private readonly VerificationOptions _options;
    private readonly ILogger<FindingsJudgeSelector> _logger;

    public FindingsJudgeSelector(IFindingsJudge llm, IFindingsJudge? jev, VerificationOptions options, ILogger<FindingsJudgeSelector> logger)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _jev = jev;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (options.Judge != FindingsJudgeMode.Llm && jev is null)
        {
            throw new ArgumentException(
                $"Verification:Judge is {options.Judge} but no Jev judge was supplied.", nameof(jev));
        }
    }

    // Jev mode asks about every vision finding. Shadow acts on the LLM, which asks only about short ones, so it reports false.
    public bool JudgesAllVisionFindings => _options.Judge switch
    {
        FindingsJudgeMode.Jev => true,
        FindingsJudgeMode.Shadow => false,
        _ => _llm.JudgesAllVisionFindings,
    };

    public async Task<SnippetJudgement> JudgeSnippetsAsync(AgentContext context, FindingsKind kind, IReadOnlyList<string> snippets, CancellationToken ct)
    {
        // R21: code snippets reach Jev only when Verification:Jev:IncludeCode is on. Otherwise they stay with the LLM, with Source "llm".
        if (_options.Judge == FindingsJudgeMode.Llm || SkipsJev(kind))
        {
            return await _llm.JudgeSnippetsAsync(context, kind, snippets, ct);
        }

        return _options.Judge == FindingsJudgeMode.Shadow
            ? await JudgeSnippetsShadowAsync(context, kind, snippets, ct)
            : await JudgeSnippetsWithJevAsync(context, kind, snippets, ct);
    }

    public async Task<VisionJudgement> JudgeVisionAsync(AgentContext context, CancellationToken ct)
    {
        return _options.Judge switch
        {
            FindingsJudgeMode.Jev => await JudgeVisionWithJevAsync(context, ct),
            FindingsJudgeMode.Shadow => await JudgeVisionShadowAsync(context, ct),
            _ => await _llm.JudgeVisionAsync(context, ct),
        };
    }

    public async Task<GroundednessJudgement> JudgeGroundednessAsync(AgentContext context, string draft, CancellationToken ct)
    {
        // U13 / R18: Jev judges sentences only when Verification:GroundednessPerSentence is on. Shadow never runs it.
        if (_options.Judge != FindingsJudgeMode.Jev || !_options.GroundednessPerSentence)
        {
            return await _llm.JudgeGroundednessAsync(context, draft, ct);
        }

        var clock = Stopwatch.StartNew();
        try
        {
            return await RunJevAsync(t => RequireJev().JudgeGroundednessAsync(context, draft, t), clock, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFallback("groundedness", ex, clock);
            var fallback = await _llm.JudgeGroundednessAsync(context, draft, ct);
            return fallback with { Source = FallbackSource };
        }
    }

    private async Task<SnippetJudgement> JudgeSnippetsWithJevAsync(AgentContext context, FindingsKind kind, IReadOnlyList<string> snippets, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            return await RunJevAsync(t => RequireJev().JudgeSnippetsAsync(context, kind, snippets, t), clock, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFallback(KindName(kind), ex, clock);
            var fallback = await _llm.JudgeSnippetsAsync(context, kind, snippets, ct);
            return fallback with { Source = FallbackSource };
        }
    }

    private async Task<VisionJudgement> JudgeVisionWithJevAsync(AgentContext context, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            return await RunJevAsync(t => RequireJev().JudgeVisionAsync(context, t), clock, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFallback("vision", ex, clock);

            // R20 fallback: the legacy rule. Findings of 20 characters or more pass with no LLM call. Shorter findings go to the LLM judge.
            var findings = context.VisionFindings ?? string.Empty;
            if (findings.Length >= LegacyVisionAutoPassLength)
            {
                return new VisionJudgement(Relevant: true, Score: null, TokensUsed: 0, Source: FallbackSource);
            }

            var fallback = await _llm.JudgeVisionAsync(context, ct);
            return fallback with { Source = FallbackSource };
        }
    }

    private async Task<SnippetJudgement> JudgeSnippetsShadowAsync(AgentContext context, FindingsKind kind, IReadOnlyList<string> snippets, CancellationToken ct)
    {
        var run = await RunShadowAsync(
            t => _llm.JudgeSnippetsAsync(context, kind, snippets, t),
            t => RequireJev().JudgeSnippetsAsync(context, kind, snippets, t),
            ct);

        if (run.Jev is { } jev)
        {
            context.AddTokens(ShadowTokensAgent, jev.TokensUsed);
        }

        LogSnippetShadow(kind, run);
        return run.Llm;
    }

    private async Task<VisionJudgement> JudgeVisionShadowAsync(AgentContext context, CancellationToken ct)
    {
        var run = await RunShadowAsync(
            t => _llm.JudgeVisionAsync(context, t),
            t => RequireJev().JudgeVisionAsync(context, t),
            ct);

        if (run.Jev is { } jev)
        {
            context.AddTokens(ShadowTokensAgent, jev.TokensUsed);
        }

        var agree = run.Jev is { } answer && answer.Relevant == run.Llm.Relevant;
        _logger.LogInformation(
            "VerifierShadow: kind=vision llmRelevant={LlmRelevant} jevScore={JevScore} jevRelevant={JevRelevant} agree={Agree} jevLatencyMs={JevLatencyMs} jevFailed={JevFailed}",
            run.Llm.Relevant,
            run.Jev?.Score,
            run.Jev?.Relevant,
            agree,
            (long)run.JevLatencyMs,
            run.Jev is null);
        return run.Llm;
    }

    // Starts the Jev judge under its budget, then waits for it. The result is not used unless it arrives within the budget.
    private async Task<T> RunJevAsync<T>(Func<CancellationToken, Task<T>> call, Stopwatch clock, CancellationToken ct)
    {
        using var jevCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jevCts.CancelAfter(_options.JevTimeout);
        return await WaitForJevAsync(call(jevCts.Token), clock, jevCts);
    }

    // Shadow (R20). Both judges start together. The LLM answer is the one acted on, and Jev is waited for only within its budget.
    // A Jev failure is recorded, never thrown, so Shadow adds no latency beyond the LLM path.
    private async Task<ShadowRun<TLlm, TJev>> RunShadowAsync<TLlm, TJev>(
        Func<CancellationToken, Task<TLlm>> llmCall,
        Func<CancellationToken, Task<TJev>> jevCall,
        CancellationToken ct) where TJev : class
    {
        var clock = Stopwatch.StartNew();
        using var jevCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jevCts.CancelAfter(_options.JevTimeout);
        var jevTask = jevCall(jevCts.Token);

        TLlm llm;
        try
        {
            llm = await llmCall(ct);
        }
        catch (Exception)
        {
            // The LLM judge is the acting path, so its failure fails the call as it does in Llm mode. Stop the Jev call too.
            jevCts.Cancel();
            throw;
        }

        TJev? jev = null;
        try
        {
            jev = await WaitForJevAsync(jevTask, clock, jevCts);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Jev failed, ran out of budget or answered late. The LLM result stands, and VerifierShadow records jevFailed.
        }

        return new ShadowRun<TLlm, TJev>(llm, jev, clock.Elapsed.TotalMilliseconds);
    }

    // Waits for Jev within Verification:JevTimeout, measured from `clock`. The wait is bounded even if the Jev judge ignores its token.
    // Throws when Jev fails, runs out of budget or answers after the budget.
    private async Task<T> WaitForJevAsync<T>(Task<T> jevTask, Stopwatch clock, CancellationTokenSource jevCts)
    {
        var remaining = _options.JevTimeout - clock.Elapsed;
        var budget = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        if (await Task.WhenAny(jevTask, budget) != jevTask)
        {
            jevCts.Cancel();
            throw new TimeoutException("Jev did not answer within Verification:JevTimeout.");
        }

        var answer = await jevTask;
        if (clock.Elapsed > _options.JevTimeout)
        {
            throw new TimeoutException("Jev answered after Verification:JevTimeout.");
        }

        return answer;
    }

    // Only the kind, the exception type and the timing are logged. The message and the customer text never reach the logger.
    private void LogFallback(string kind, Exception ex, Stopwatch clock) =>
        _logger.LogWarning(
            "VerifierJevFallback: kind={Kind} exceptionType={ExceptionType} elapsedMs={ElapsedMs}. Judging with the LLM instead.",
            kind,
            ex.GetType().Name,
            clock.ElapsedMilliseconds);

    // Labels, scores and counts only, never snippet text.
    private void LogSnippetShadow(FindingsKind kind, ShadowRun<SnippetJudgement, SnippetJudgement> run)
    {
        var llm = run.Llm;
        var jev = run.Jev;
        var threshold = _options.MinRelevance;
        var jevScores = jev?.Scores ?? Array.Empty<double>();
        var jevKept = jevScores.Count(score => score >= threshold);
        double? jevTopScore = jevScores.Count > 0 ? jevScores.Max() : null;

        // Agree when the LLM's pick is one Jev kept. When the LLM picked nothing, Jev must also have kept nothing.
        var agree = llm.BestIndex is { } pick
            ? pick < jevScores.Count && jevScores[pick] >= threshold
            : jev is not null && jevKept == 0;

        _logger.LogInformation(
            "VerifierShadow: kind={Kind} llmPick={LlmPick} jevKept={JevKept} jevTopScore={JevTopScore} agree={Agree} jevLatencyMs={JevLatencyMs} jevFailed={JevFailed}",
            KindName(kind),
            llm.BestIndex,
            jevKept,
            jevTopScore,
            agree,
            (long)run.JevLatencyMs,
            jev is null);
    }

    private bool SkipsJev(FindingsKind kind) => kind == FindingsKind.Code && !_options.Jev.IncludeCode;

    private IFindingsJudge RequireJev() =>
        _jev ?? throw new InvalidOperationException("Verification:Judge needs a Jev judge, and none was supplied.");

    private static string KindName(FindingsKind kind) => kind.ToString().ToLowerInvariant();

    // Shadow's Jev side. Jev is null when Jev failed, ran out of budget or answered late.
    private sealed record ShadowRun<TLlm, TJev>(TLlm Llm, TJev? Jev, double JevLatencyMs) where TJev : class;
}
