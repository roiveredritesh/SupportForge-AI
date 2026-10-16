using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Verification;

namespace SupportForge.Agents;

public sealed class VisionAnalyzerVerifier : IAgent
{
    // U11: the judge sits behind IFindingsJudge. LlmFindingsJudge holds this verifier's vision judge prompt.
    private readonly IFindingsJudge _judge;
    // U18: Verification:IndirectThreshold drives the injection check below.
    private readonly VerificationOptions _options;
    private readonly ILogger<VisionAnalyzerVerifier> _logger;
    public string Name => "VisionAnalyzerVerifier";

    public VisionAnalyzerVerifier(IFindingsJudge judge, VerificationOptions options, ILogger<VisionAnalyzerVerifier> logger)
    {
        _judge = judge;
        _options = options;
        _logger = logger;
    }

    public VisionAnalyzerVerifier(IFindingsJudge judge, ILogger<VisionAnalyzerVerifier> logger) : this(judge, new VerificationOptions(), logger) { }

    // U11: existing call shape. Wraps the client in the LLM judge, which is exactly what this verifier did before.
    public VisionAnalyzerVerifier(ILlmChatClient llm, ILogger<VisionAnalyzerVerifier> logger) : this(new LlmFindingsJudge(llm), logger) { }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} hasScreenshot={HasScreenshot}", Name, context.ProjectId, !string.IsNullOrEmpty(context.ScreenshotBase64));
        try
        {
            if (string.IsNullOrEmpty(context.ScreenshotBase64))
            {
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, no screenshot", Name, sw.ElapsedMilliseconds);
                return context;
            }

            var v = context.VisionVerification;
            var findings = context.VisionFindings;

            // U18: reset on every attempt, so a retry never inherits the previous attempt's count.
            v.InjectionDropped = 0;

            if (string.IsNullOrWhiteSpace(findings))
            {
                Fail(v, "Vision analysis returned no findings for a provided screenshot.");
            }
            else if (findings.Length < 20 || _judge.JudgesAllVisionFindings)
            {
                // U11: the 20-character gate is the LLM judge's policy (JudgesAllVisionFindings == false). A judge
                // that opts in to every finding is asked about all of them, long ones included.
                var judgement = await _judge.JudgeVisionAsync(context, ct);
                context.AddTokens(Name, judgement.TokensUsed);
                v.Source = judgement.Source;
                // U12 (R17): a score-producing judge's score is kept for telemetry. The verdict still comes from Relevant.
                if (judgement.Score is double s) v.Scores = new[] { s };

                // U18 (R26): flagged findings fail whatever their relevance. Only a judge that screens sets InjectionScore, so the
                // LLM judge and the fallbacks never reach this check. The index of the findings is always 0.
                if (judgement.InjectionScore is double injection && injection >= _options.IndirectThreshold)
                {
                    v.InjectionDropped = 1;

                    // U18: the position and counts only, never finding text.
                    _logger.LogWarning("IndirectInjectionDropped: verifier={Verifier} project={ProjectId} indexes={Indexes}", Name, context.ProjectId, "0");
                    Activity.Current?.SetTag("verify.injection_dropped", 1);
                    Fail(v, "Vision findings were flagged as containing instructions addressed to an AI assistant.");
                }
                else if (judgement.Relevant) v.Status = VerificationStatus.Passed;
                else Fail(v, "LLM judge found the vision findings to be a non-answer.");

                // U14: span tags carry labels and scores only, never finding text.
                var activity = Activity.Current;
                activity?.SetTag("verify.source", v.Source);
                if (v.Scores is { Count: > 0 } tagScores) activity?.SetTag("verify.top_score", tagScores.Max());
            }
            else
            {
                v.Status = VerificationStatus.Passed;
            }

            if (v.Status == VerificationStatus.FailedRetrying)
                _logger.LogWarning("{Agent} retrying: attempt={Attempt} reason={Reason}", Name, v.Attempts, v.Reason);

            // See KbResearcherVerifier's matching comment: DrafterAgent.BuildUserPrompt has no
            // visibility into VisionVerification.Status, so a final "not a real answer" verdict has to
            // be enforced here by actually removing the rejected findings.
            if (v.Status == VerificationStatus.FailedFinal)
                context.VisionFindings = null;

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
}
