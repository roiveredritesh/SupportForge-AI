namespace SupportForge.Agents;

public sealed class CodeAnalyzerVerifier : IAgent
{
    private const string JudgeSystemPrompt = """
        You judge whether a single retrieved source-code snippet is actually relevant to a
        customer's code-related support question. Respond with only "yes" or "no".
        """;

    private readonly ILlmChatClient _llm;
    public string Name => "CodeAnalyzerVerifier";

    public CodeAnalyzerVerifier(ILlmChatClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (context.Intent is not ("code_issue" or "code_question")) return context;

        var v = context.CodeVerification;

        if (context.CodeSnippets.Count == 0)
        {
            Fail(v, "No code snippets were retrieved for a code-related query.");
        }
        else if (context.CodeSnippets.Count == 1)
        {
            var relevant = await JudgeAsync(context, ct);
            if (relevant) v.Status = VerificationStatus.Passed;
            else Fail(v, "LLM judge found the single retrieved code snippet not relevant to the query.");
        }
        else
        {
            v.Status = VerificationStatus.Passed;
        }

        return context;
    }

    private static void Fail(VerificationResult v, string reason)
    {
        v.Status = v.Attempts < 2 ? VerificationStatus.FailedRetrying : VerificationStatus.FailedFinal;
        v.Reason = reason;
    }

    private async Task<bool> JudgeAsync(AgentContext context, CancellationToken ct)
    {
        var userPrompt = $"""
            Customer question: {context.Query}
            Retrieved code snippet: {context.CodeSnippets[0]}
            """;
        var verdict = await _llm.CompleteAsync(JudgeSystemPrompt, userPrompt, ct);
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return verdict.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
