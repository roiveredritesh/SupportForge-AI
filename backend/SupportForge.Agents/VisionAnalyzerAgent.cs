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
            context.VisionFindings = "Vision analysis unavailable: the configured chat model does not support vision.";
            return context;
        }

        context.VisionFindings = await _tool.AnalyzeAsync(context.ScreenshotBase64, context.VisionVerification.Attempts > 0, ct);
        context.TotalTokensUsed += _tool.LastTotalTokens;
        context.VisionVerification.Attempts++;
        return context;
    }
}
