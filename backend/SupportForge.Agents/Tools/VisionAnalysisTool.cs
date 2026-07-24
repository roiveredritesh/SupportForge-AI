namespace SupportForge.Agents.Tools;

public sealed class VisionAnalysisTool
{
    private readonly ILlmClient _llm;

    public VisionAnalysisTool(ILlmClient llm) => _llm = llm;

    public int LastTotalTokens { get; private set; }

    public async Task<string> AnalyzeAsync(string base64Image, CancellationToken ct = default)
    {
        var result = await _llm.AnalyzeImageAsync(base64Image,
            "Describe any error messages, stack traces, or UI state visible in this screenshot relevant to a support ticket.",
            ct);
        LastTotalTokens = _llm.LastTotalTokens;
        return result;
    }
}
