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
            // U10: bias retrieval toward the customer's stated version when given -- KbSearchTool
            // falls back to an unfiltered query if nothing matches this filter, so an untagged corpus
            // still retrieves normally.
            var metadataFilter = string.IsNullOrEmpty(context.ProductVersion)
                ? null
                : new Dictionary<string, string> { ["version"] = context.ProductVersion };
            var results = await _tool.SearchAsync(context.ProjectId, context.Query, topK, metadataFilter, ct);

            context.KbSnippets.Clear();
            foreach (var (text, source) in results)
            {
                context.KbSnippets.Add(text);
                context.Sources.Add(($"KB: {DescribeSource(source)}", source));
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

    // Path.GetFileName assumes a filesystem path; a Website KB source is a URL, and doc sites almost
    // always use trailing-slash routes (e.g. ".../getting-started/"), which GetFileName treats as "no
    // filename" and returns "" for -- producing a blank "KB: " label. Use the last non-empty URL segment
    // instead, falling back to the host for a bare "https://example.com/" source.
    private static string DescribeSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return last ?? uri.Host;
        }
        return Path.GetFileName(source);
    }
}
