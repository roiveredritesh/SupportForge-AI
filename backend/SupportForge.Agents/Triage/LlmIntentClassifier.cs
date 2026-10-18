namespace SupportForge.Agents.Triage;

// U2 (JEV intent classification plan): the LLM triage call, moved verbatim out of TriageAgent behind
// IIntentClassifier. Behaviour is unchanged: same prompt, same normalization, same "unclear" fallback.
public sealed class LlmIntentClassifier : IIntentClassifier
{
    private static readonly char[] Decoration = [' ', '\t', '\r', '\n', '"', '\'', '`', '*', '.', ':', '#'];

    private readonly ILlmChatClient _llm;

    public LlmIntentClassifier(ILlmChatClient llm)
    {
        _llm = llm;
    }

    public async Task<IntentDecision> ClassifyAsync(AgentContext context, IReadOnlyList<string> labels, CancellationToken ct)
    {
        // `labels` is ignored: the system prompt below already names the six labels, and the LLM picks exactly one.
        const string systemPrompt = """
            You classify support queries into exactly one label:
            - "kb_question": product/feature/process questions answered by documentation, not the source code.
            - "code_issue": a bug report or error involving the project's source code.
            - "code_question": a question about the project's source code that is not a bug report — e.g. "how does X work",
              "explain function Y", "where is Z implemented", "which class, method, function, decorator, exception, module, middleware or service does W",
              architecture questions.
            - "screenshot_error": the query is about an attached screenshot showing an error or UI state.
            - "greeting": a greeting, thanks, farewell, or other pleasantry with no actual support question
              in it — e.g. "hi", "hello", "thanks!", "that's all for now".
            - "unclear": the query has no answerable question — it is too vague or missing the context it needs.
              When it asks something specific that more than one label could answer, pick the most likely label instead.
              A question about which part of the code does something is a "code_question", even when it does not
              name the project.
            Respond with only the label, nothing else.
            """;

        var intent = await _llm.CompleteAsync(systemPrompt, context.Query, ct);
        var normalized = intent.Trim(Decoration).ToLowerInvariant();
        // Anything we can't map is treated as "unclear" so the downstream gates no-op and the
        // Drafter asks for clarification, rather than answering from empty retrieval context.
        var label = TriageLabels.IsKnown(normalized) ? normalized : TriageLabels.Unclear;
        // The LLM gives no probabilities, so the chosen label is certain (1.0) and nothing else is scored.
        return new IntentDecision(
            new Dictionary<string, double> { [label] = 1.0 },
            1.0,
            new Dictionary<string, double>(),
            _llm.LastTotalTokens,
            "llm");
    }
}
