using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents;

public sealed class VisionAnalyzerVerifier : IAgent
{
    private const string JudgeSystemPrompt = """
        You judge whether a vision-analysis description meaningfully describes an error, stack trace,
        or relevant UI state, as opposed to a vague non-answer. Respond with only "yes" or "no".
        """;

    private readonly ILlmChatClient _llm;
    private readonly ILogger<VisionAnalyzerVerifier> _logger;
    public string Name => "VisionAnalyzerVerifier";

    public VisionAnalyzerVerifier(ILlmChatClient llm, ILogger<VisionAnalyzerVerifier> logger)
    {
        _llm = llm;
        _logger = logger;
    }

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

            if (string.IsNullOrWhiteSpace(findings))
            {
                Fail(v, "Vision analysis returned no findings for a provided screenshot.");
            }
            else if (findings.Length < 20)
            {
                var relevant = await JudgeAsync(context, ct);
                if (relevant) v.Status = VerificationStatus.Passed;
                else Fail(v, "LLM judge found the vision findings to be a non-answer.");
            }
            else
            {
                v.Status = VerificationStatus.Passed;
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

    private async Task<bool> JudgeAsync(AgentContext context, CancellationToken ct)
    {
        var userPrompt = $"""
            Customer question: {context.Query}
            Vision analysis findings: {context.VisionFindings}
            """;
        var verdict = await _llm.CompleteAsync(JudgeSystemPrompt, userPrompt, ct);
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return verdict.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
