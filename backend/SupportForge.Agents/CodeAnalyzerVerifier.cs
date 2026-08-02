using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerVerifier : IAgent
{
    private const string JudgeSystemPrompt = """
        You judge whether a single retrieved source-code snippet is actually relevant to a
        customer's code-related support question. Respond with only "yes" or "no".
        """;

    private readonly ILlmChatClient _llm;
    private readonly ILogger<CodeAnalyzerVerifier> _logger;
    public string Name => "CodeAnalyzerVerifier";

    public CodeAnalyzerVerifier(ILlmChatClient llm, ILogger<CodeAnalyzerVerifier> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", Name, context.ProjectId, context.Intent);
        try
        {
            if (context.Intent is not ("code_issue" or "code_question"))
            {
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, intent={Intent} not applicable", Name, sw.ElapsedMilliseconds, context.Intent);
                return context;
            }

            var v = context.CodeVerification;

            if (context.CodeSnippets.Count == 0)
            {
                Fail(v, "No code snippets were retrieved for a code-related query.");
            }
            else
            {
                // Vector search always returns topK nearest neighbors with no relevance floor, so a
                // non-empty result set doesn't mean the content is on-topic. Judge the top match: if
                // even the closest snippet isn't relevant, the rest (farther away) won't be either.
                var relevant = await JudgeAsync(context, ct);
                if (relevant) v.Status = VerificationStatus.Passed;
                else Fail(v, "LLM judge found the top retrieved code snippet not relevant to the query.");
            }

            if (v.Status == VerificationStatus.FailedRetrying)
                _logger.LogWarning("{Agent} retrying: attempt={Attempt} reason={Reason}", Name, v.Attempts, v.Reason);

            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: status={Status} reason={Reason}", Name, sw.ElapsedMilliseconds, v.Status, v.Reason);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
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
