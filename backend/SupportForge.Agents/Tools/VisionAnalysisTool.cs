namespace SupportForge.Agents.Tools;

public sealed class VisionAnalysisTool
{
    private readonly ILlmClient _llm;

    public VisionAnalysisTool(ILlmClient llm) => _llm = llm;

    public int LastTotalTokens { get; private set; }

    public async Task<string> AnalyzeAsync(string base64Image, bool detailed = false, CancellationToken ct = default)
    {
        var prompt = detailed
            ? "Describe in detail any error messages, stack traces, log output, or UI state visible in this screenshot relevant to a support ticket. Include exact text where legible."
            : "Describe any error messages, stack traces, or UI state visible in this screenshot relevant to a support ticket.";
        var result = await _llm.AnalyzeImageAsync(base64Image, prompt, ct);
        LastTotalTokens = _llm.LastTotalTokens;
        return result;
    }
}
