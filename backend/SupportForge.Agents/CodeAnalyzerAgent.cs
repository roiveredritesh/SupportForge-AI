using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerAgent : IAgent
{
    private readonly ICodeGraphQueryTool _tool;
    private readonly ILogger<CodeAnalyzerAgent> _logger;
    public string Name => "CodeAnalyzer";

    public CodeAnalyzerAgent(ICodeGraphQueryTool tool, ILogger<CodeAnalyzerAgent> logger)
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
            if (context.Intent is not ("code_issue" or "code_question"))
            {
                _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: skipped, intent={Intent} not applicable", Name, sw.ElapsedMilliseconds, context.Intent);
                return context;
            }

            var retrying = context.CodeVerification.Attempts > 0;
            // WS4 (retrieval-pipeline remediation plan): CrossReferenceAgent may have extracted
            // code-searchable terms from KB findings (e.g. an API route) -- fold them into the question
            // the code graph searches on, instead of Code retrieving from the raw customer question alone.
            var question = string.IsNullOrEmpty(context.CodeQueryAugmentation)
                ? context.Query
                : $"{context.Query} (related terms from documentation: {context.CodeQueryAugmentation})";
            var output = await _tool.QueryAsync(context.ProjectId, question, retrying, ct);

            context.CodeSnippets.Clear();
            if (!string.IsNullOrEmpty(output))
            {
                context.CodeSnippets.Add(output);
                context.Sources.Add(("Code: project graph", context.ProjectId));
            }
            context.CodeVerification.Attempts++;
            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: retrieved {SnippetCount} snippets, attempt={Attempt}", Name, sw.ElapsedMilliseconds, context.CodeSnippets.Count, context.CodeVerification.Attempts);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
