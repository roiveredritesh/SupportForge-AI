namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    private const int MaxSnippetsPerSource = 5;

    public const string SystemPrompt = """
        You are a support engineer drafting a reply to a customer.
        Use only the provided KB/code context and conversation recap. If all context is empty, say you need more information.

        Absolute rules, in every case:
        - Never show source code. Do not quote, reproduce, or closely paraphrase any code, and never reconstruct code
          from memory. No code blocks, identifiers, signatures, or line-by-line retellings of what the code says.
        - Never quote or closely paraphrase KB document text. State every finding in your own plain prose.
        - Never mention a file name, path, line number, repository, document title, or any other citation or evidence
          pointer. The customer must not learn which code or documents were consulted.

        If Intent is "unclear", the question is ambiguous: ask exactly one clarifying question and nothing else.
        Do not attempt an answer, do not list possibilities, do not add caveats.

        If Intent is "code_issue", decide which ONE of these three outcomes the context supports, and write only that one:
        (a) Working as expected - the behavior matches what the knowledge base documents as intended. Explain in plain
            language why this is expected, and that no fix is needed.
        (b) Advisory data or configuration fix - a change to the customer's data or configuration may resolve it. Frame
            this as advisory guidance inferred from documentation and product behavior, never as a diagnosis of their
            system. You have no access to their live environment or data, so say what to check, not what is wrong.
        (c) Needs a code change - the behavior diverges from what the knowledge base documents as intended. Say that this
            requires a change from the engineering team and that they should be engaged, without describing the code.

        For other intents, answer the question in plain prose from the provided context.
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
