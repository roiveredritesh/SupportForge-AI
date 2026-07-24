namespace SupportForge.Agents;

public sealed class TriageAgent : IAgent
{
    private readonly ILlmClient _llm;
    public string Name => "Triage";

    public TriageAgent(ILlmClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        const string systemPrompt = """
            You classify support queries into exactly one label: "kb_question", "code_issue", or "screenshot_error".
            Respond with only the label, nothing else.
            """;

        var intent = await _llm.CompleteAsync(systemPrompt, context.Query, ct);
        context.Intent = intent.Trim();
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return context;
    }
}
