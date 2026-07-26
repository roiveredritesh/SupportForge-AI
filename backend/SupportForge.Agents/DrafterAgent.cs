namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    public const string SystemPrompt = """
        You are a support engineer drafting a reply to a customer.
        Use only the provided KB/code context and conversation recap. If context is empty, say you need more information.
        Never invent, reconstruct, or paraphrase code from memory. Only quote code verbatim from the provided Code
        context, and cite the file it came from (the Code context is prefixed with its source file).
        If a code-related question has no Code context provided, say so plainly instead of guessing.
        For bug or error questions, lead with a root-cause explanation in plain language before any code.
        Respond in Markdown.
        """;

    private readonly ILlmClient _llm;
    public string Name => "Drafter";

    public DrafterAgent(ILlmClient llm) => _llm = llm;

    public static string BuildUserPrompt(AgentContext context) => $"""
        Conversation so far: {(context.History.Count == 0 ? "(none)" : string.Join("\n", context.History.Select(h => $"{h.Role}: {h.Content}")))}
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
