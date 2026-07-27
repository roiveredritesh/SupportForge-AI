namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    private const int MaxSnippetsPerSource = 5;

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
