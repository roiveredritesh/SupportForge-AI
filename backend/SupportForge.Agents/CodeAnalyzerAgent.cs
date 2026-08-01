using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerAgent : IAgent
{
    private readonly CodeSearchTool _tool;
    private readonly ILogger<CodeAnalyzerAgent> _logger;
    public string Name => "CodeAnalyzer";

    public CodeAnalyzerAgent(CodeSearchTool tool, ILogger<CodeAnalyzerAgent> logger)
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

            var topK = context.CodeVerification.Attempts > 0 ? 10 : 5;
            var results = await _tool.SearchAsync(context.ProjectId, context.Query, topK, ct: ct);

            context.CodeSnippets.Clear();
            foreach (var (text, file) in results)
            {
                context.CodeSnippets.Add($"// {file}\n{text}");
                context.Sources.Add(($"Code: {file}", file));
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
