using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents;

public sealed partial class DrafterAgent : IAgent
{
    private const int MaxSnippetsPerSource = 5;
    private const int VerbatimRunLength = 40;

    public const string LeakFallback =
        "I can't share code or document excerpts directly. I've looked into this, but I'll need someone from the team to walk you through the specifics - please reach out to them and they can pick it up from here.";

    [GeneratedRegex(@"[\w.\\/-]+\.(cs|ts|tsx|js|jsx|py|java|go|rb|php|rs|kt|swift|scala|sql|c|h|cpp|hpp)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceFileRef();

    private static readonly HashSet<string> FrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.js", "next.js", "nuxt.js", "vue.js", "express.js",
        "d3.js", "three.js", "react.js", "ember.js", "backbone.js",
    };

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

        If Intent is "code_question", answer from the knowledge base and documentation context only: explain the
        concept, behavior, or architecture in your own plain language. Code context is for your own understanding
        only - never quote, describe, summarize, or refer to it in the answer. If the question can only be answered
        by naming a code location or implementation detail (for example "where is this implemented"), say that this
        specific implementation detail is not something you can share, and point the customer at the documented
        behavior you do have; if no documentation covers it, direct them to the engineering team.

        For other intents, answer the question in plain prose from the provided context.
        Respond in Markdown.
        """;

    private readonly ILlmChatClient _llm;
    private readonly ILogger<DrafterAgent> _logger;
    public string Name => "Drafter";

    public DrafterAgent(ILlmChatClient llm, ILogger<DrafterAgent> logger)
    {
        _llm = llm;
        _logger = logger;
    }

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
    public static bool LooksLikeLeak(string draft, AgentContext context)
    {
        if (string.IsNullOrEmpty(draft)) return false;
        if (draft.Contains("```")) return true;

        // Echoing a filename the customer themselves wrote isn't a leak, and ".js" frameworks aren't files.
        var customerText = string.Join("\n", context.History.Select(h => h.Content).Append(context.Query));
        if (SourceFileRef().Matches(draft).Any(m =>
                !FrameworkNames.Contains(m.Value)
                && !customerText.Contains(m.Value, StringComparison.OrdinalIgnoreCase)))
            return true;

        return context.CodeSnippets.Concat(context.KbSnippets).Any(s => s.Length >= VerbatimRunLength
            && Enumerable.Range(0, s.Length - VerbatimRunLength + 1)
                .Any(i => draft.AsSpan().Contains(s.AsSpan(i, VerbatimRunLength), StringComparison.Ordinal)));
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", Name, context.ProjectId, context.Intent);
        try
        {
            var draft = await _llm.CompleteAsync(SystemPrompt, BuildUserPrompt(context), ct);
            var leaked = LooksLikeLeak(draft, context);
            context.Draft = leaked ? LeakFallback : draft;
            context.Confidence = leaked ? 0.0 : ComputeConfidence(context);
            context.TotalTokensUsed += _llm.LastTotalTokens;
            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: leaked={Leaked} confidence={Confidence}", Name, sw.ElapsedMilliseconds, leaked, context.Confidence);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
