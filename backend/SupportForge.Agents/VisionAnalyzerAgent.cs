using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class VisionAnalyzerAgent : IAgent
{
    private readonly VisionAnalysisTool _tool;
    private readonly ILogger<VisionAnalyzerAgent> _logger;
    public string Name => "VisionAnalyzer";

    public VisionAnalyzerAgent(VisionAnalysisTool tool, ILogger<VisionAnalyzerAgent> logger)
    {
        _tool = tool;
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

            if (!_tool.SupportsVision)
            {
                // Decide verification state explicitly here rather than letting it fall out of
                // VisionAnalyzerVerifier's message-length heuristic -- that heuristic is coincidental
                // for this specific sentinel and would misclassify it (and retry-loop forever, since
                // Attempts never advances on this path) if the message text ever changed.
                context.VisionFindings = "Vision analysis unavailable: the configured chat model does not support vision.";
                context.VisionVerification.Status = VerificationStatus.Passed;
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, model does not support vision", Name, sw.ElapsedMilliseconds);
                return context;
            }

            context.VisionFindings = await _tool.AnalyzeAsync(context.ScreenshotBase64, context.VisionVerification.Attempts > 0, ct);
            context.TotalTokensUsed += _tool.LastTotalTokens;
            context.VisionVerification.Attempts++;
            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: findings length={FindingsLength}, attempt={Attempt}", Name, sw.ElapsedMilliseconds, context.VisionFindings?.Length ?? 0, context.VisionVerification.Attempts);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
