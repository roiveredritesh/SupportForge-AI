namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    public const string SystemPrompt = """
        You are a support engineer drafting a reply to a customer.
        Use only the provided KB/code context. If context is empty, say you need more information.
        Respond in Markdown.
        """;

    private readonly ILlmClient _llm;
    public string Name => "Drafter";

    public DrafterAgent(ILlmClient llm) => _llm = llm;

    public static string BuildUserPrompt(AgentContext context) => $"""
        Customer question: {context.Query}
        Intent: {context.Intent}
        KB context: {string.Join("\n---\n", context.KbSnippets)}
        Code context: {string.Join("\n---\n", context.CodeSnippets)}
        Vision findings: {context.VisionFindings}
        """;

    public static double ComputeConfidence(AgentContext context) =>
        context.KbSnippets.Count + context.CodeSnippets.Count > 0 ? 0.8 : 0.4;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        context.Draft = await _llm.CompleteAsync(SystemPrompt, BuildUserPrompt(context), ct);
        context.Confidence = ComputeConfidence(context);
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return context;
    }
}
