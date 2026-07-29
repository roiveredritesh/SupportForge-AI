namespace SupportForge.Agents;

public sealed class KbResearcherVerifier : IAgent
{
    private const string JudgeSystemPrompt = """
        You judge whether a single retrieved knowledge-base snippet is actually relevant to a
        customer's support question. Respond with only "yes" or "no".
        """;

    private readonly ILlmClient _llm;
    public string Name => "KbResearcherVerifier";

    public KbResearcherVerifier(ILlmClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (context.Intent is not ("kb_question" or "code_issue" or "code_question")) return context;

        var v = context.KbVerification;

        if (context.KbSnippets.Count == 0)
        {
            Fail(v, "No KB snippets were retrieved.");
        }
        else if (context.KbSnippets.Count == 1)
        {
            var relevant = await JudgeAsync(context, ct);
            if (relevant) v.Status = VerificationStatus.Passed;
            else Fail(v, "LLM judge found the single retrieved snippet not relevant to the query.");
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
            Retrieved snippet: {context.KbSnippets[0]}
            """;
        var verdict = await _llm.CompleteAsync(JudgeSystemPrompt, userPrompt, ct);
        context.TotalTokensUsed += _llm.LastTotalTokens;
        return verdict.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
