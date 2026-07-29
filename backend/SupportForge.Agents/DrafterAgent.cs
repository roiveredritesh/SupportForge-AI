using System.Text.RegularExpressions;

namespace SupportForge.Agents;

public sealed partial class DrafterAgent : IAgent
{
    private const int MaxSnippetsPerSource = 5;
    private const int VerbatimRunLength = 40;

    public const string LeakFallback =
        "I can't share code or document excerpts directly. I've looked into this, but I'll need someone from the team to walk you through the specifics - please reach out to them and they can pick it up from here.";

    [GeneratedRegex(@"[\w.\\/-]+\.(cs|ts|tsx|js|jsx|py|java|go|rb|php|rs|kt|swift|scala|sql|c|h|cpp|hpp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceFileRef();

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

    /// <summary>Safety net for when the LLM ignores the no-code rules in <see cref="SystemPrompt"/>.</summary>
    public static bool LooksLikeLeak(string draft, IEnumerable<string> snippets)
    {
        if (string.IsNullOrEmpty(draft)) return false;
        if (draft.Contains("```")) return true;
        if (SourceFileRef().IsMatch(draft)) return true;

        return snippets.Any(s => s.Length >= VerbatimRunLength
            && Enumerable.Range(0, s.Length - VerbatimRunLength + 1)
                .Any(i => draft.AsSpan().Contains(s.AsSpan(i, VerbatimRunLength), StringComparison.Ordinal)));
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var draft = await _llm.CompleteAsync(SystemPrompt, BuildUserPrompt(context), ct);
        context.Draft = LooksLikeLeak(draft, context.CodeSnippets.Concat(context.KbSnippets)) ? LeakFallback : draft;
        context.Confidence = ComputeConfidence(context);
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return context;
    }
}
