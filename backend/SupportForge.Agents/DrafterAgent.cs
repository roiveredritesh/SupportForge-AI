namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    private readonly ILlmClient _llm;
    public string Name => "Drafter";

    public DrafterAgent(ILlmClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        const string systemPrompt = """
            You are a support engineer drafting a reply to a customer.
            Use only the provided KB/code context. If context is empty, say you need more information.
            Respond in Markdown.
            """;

        var userPrompt = $"""
            Customer question: {context.Query}
            Intent: {context.Intent}
            KB context: {string.Join("\n---\n", context.KbSnippets)}
            Code context: {string.Join("\n---\n", context.CodeSnippets)}
            Vision findings: {context.VisionFindings}
            """;

        context.Draft = await _llm.CompleteAsync(systemPrompt, userPrompt, ct);
        context.Confidence = context.KbSnippets.Count + context.CodeSnippets.Count > 0 ? 0.8 : 0.4;
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return context;
    }
}
