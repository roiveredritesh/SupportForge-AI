using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class VisionAnalyzerAgent : IAgent
{
    private readonly VisionAnalysisTool _tool;
    public string Name => "VisionAnalyzer";

    public VisionAnalyzerAgent(VisionAnalysisTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.ScreenshotBase64)) return context;

        if (!_tool.SupportsVision)
        {
            // Decide verification state explicitly here rather than letting it fall out of
            // VisionAnalyzerVerifier's message-length heuristic -- that heuristic is coincidental
            // for this specific sentinel and would misclassify it (and retry-loop forever, since
            // Attempts never advances on this path) if the message text ever changed.
            context.VisionFindings = "Vision analysis unavailable: the configured chat model does not support vision.";
            context.VisionVerification.Status = VerificationStatus.Passed;
            return context;
        }

        context.VisionFindings = await _tool.AnalyzeAsync(context.ScreenshotBase64, context.VisionVerification.Attempts > 0, ct);
        context.TotalTokensUsed += _tool.LastTotalTokens;
        context.VisionVerification.Attempts++;
        return context;
    }
}
