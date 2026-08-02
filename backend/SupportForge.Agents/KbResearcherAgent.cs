using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class KbResearcherAgent : IAgent
{
    private readonly KbSearchTool _tool;
    private readonly ILogger<KbResearcherAgent> _logger;
    public string Name => "KbResearcher";

    public KbResearcherAgent(KbSearchTool tool, ILogger<KbResearcherAgent> logger)
    {
        _tool = tool;
        _logger = logger;
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", Name, context.ProjectId, context.Intent);
        try
        {
            if (context.Intent is not ("kb_question" or "code_issue" or "code_question"))
            {
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, intent={Intent} not applicable", Name, sw.ElapsedMilliseconds, context.Intent);
                return context;
            }

            var topK = context.KbVerification.Attempts > 0 ? 10 : 5;
            var results = await _tool.SearchAsync(context.ProjectId, context.Query, topK, ct: ct);

            context.KbSnippets.Clear();
            foreach (var (text, source) in results)
            {
                context.KbSnippets.Add(text);
                context.Sources.Add(($"KB: {Path.GetFileName(source)}", source));
            }
            context.KbVerification.Attempts++;
            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: retrieved {SnippetCount} snippets, attempt={Attempt}", Name, sw.ElapsedMilliseconds, context.KbSnippets.Count, context.KbVerification.Attempts);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
