using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents;

/// <summary>
/// WS4 (retrieval-pipeline remediation plan): runs after <see cref="KbResearcherVerifier"/> and before
/// <see cref="CodeAnalyzerAgent"/> in <see cref="CoordinatorPipeline"/>'s new topology, so a KB finding
/// (e.g. "the v2/auth endpoint") can inform where Code searches instead of the two branches retrieving
/// in full isolation. No-ops (passthrough) when Code wouldn't run anyway or KB found nothing to work
/// from -- same self-gating pattern every existing specialist agent already uses.
/// </summary>
public sealed class CrossReferenceAgent : IAgent
{
    private const string ExtractionSystemPrompt = """
        Extract concrete, code-searchable terms (API routes, endpoint paths, function or class names,
        error codes) mentioned in a knowledge-base excerpt that are relevant to a customer's question.
        Respond with a short comma-separated list of terms, or "none" if there is nothing concrete to
        extract. Respond with nothing else.
        """;

    private readonly ILlmChatClient _llm;
    private readonly ILogger<CrossReferenceAgent> _logger;
    public string Name => "CrossReference";

    public CrossReferenceAgent(ILlmChatClient llm, ILogger<CrossReferenceAgent> logger)
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
            if (context.Intent is not ("code_issue" or "code_question") || context.KbSnippets.Count == 0)
            {
                _logger.LogInformation(
                    "{Agent} completed in {ElapsedMs}ms: skipped, intent={Intent} kbSnippets={Count}",
                    Name, sw.ElapsedMilliseconds, context.Intent, context.KbSnippets.Count);
                return context;
            }

            var userPrompt = $"""
                Customer question: {context.Query}
                Knowledge-base excerpt: {context.KbSnippets[0]}
                """;
            var terms = await _llm.CompleteAsync(ExtractionSystemPrompt, userPrompt, ct);
            context.TotalTokensUsed += _llm.LastTotalTokens;

            var trimmed = terms.Trim();
            context.CodeQueryAugmentation = trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : trimmed;

            _logger.LogInformation(
                "{Agent} completed in {ElapsedMs}ms: augmentation={HasAugmentation}",
                Name, sw.ElapsedMilliseconds, context.CodeQueryAugmentation is not null);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
