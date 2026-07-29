namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    private const int MaxSnippetsPerSource = 5;

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
        KB context: {string.Join("\n---\n", context.KbSnippets.Take(MaxSnippetsPerSource))}
        Code context: {string.Join("\n---\n", context.CodeSnippets.Take(MaxSnippetsPerSource))}
        Vision findings: {context.VisionFindings}
        """;

    public static double ComputeConfidence(AgentContext context)
    {
        var applicable = new[] { context.KbVerification, context.CodeVerification, context.VisionVerification }
            .Where(v => v.Status != VerificationStatus.NotRun)
            .ToList();

        if (applicable.Count == 0) return 0.3;

        var passed = applicable.Count(v => v.Status == VerificationStatus.Passed);
        return 0.2 + 0.7 * passed / applicable.Count;
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        context.Draft = await _llm.CompleteAsync(SystemPrompt, BuildUserPrompt(context), ct);
        context.Confidence = ComputeConfidence(context);
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return context;
    }
}
