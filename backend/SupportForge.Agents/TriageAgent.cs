namespace SupportForge.Agents;

public sealed class TriageAgent : IAgent
{
    private readonly ILlmClient _llm;
    public string Name => "Triage";

    public TriageAgent(ILlmClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        const string systemPrompt = """
            You classify support queries into exactly one label:
            - "kb_question": product/feature/process questions answered by documentation, not the source code.
            - "code_issue": a bug report or error involving the project's source code.
            - "code_question": a question about the project's source code that is not a bug report — e.g. "how does X work",
              "explain function Y", "where is Z implemented", architecture questions.
            - "screenshot_error": the query is about an attached screenshot showing an error or UI state.
            - "unclear": you cannot confidently place the query in any of the labels above — it is too vague,
              missing context, or plausibly fits more than one category.
            Respond with only the label, nothing else.
            """;

        var intent = await _llm.CompleteAsync(systemPrompt, context.Query, ct);
        context.Intent = intent.Trim();
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return context;
    }
}
