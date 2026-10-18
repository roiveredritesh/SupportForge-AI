using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed partial class CodeAnalyzerAgent : IAgent
{
    private readonly ICodeGraphQueryTool _tool;
    private readonly ILogger<CodeAnalyzerAgent> _logger;
    public string Name => "CodeAnalyzer";

    public CodeAnalyzerAgent(ICodeGraphQueryTool tool, ILogger<CodeAnalyzerAgent> logger)
    {
        _tool = tool;
        _logger = logger;
    }

    [GeneratedRegex(@"^NODE \S+ \[src=(?<file>\S+) ")]
    private static partial Regex NodeFile();

    // The graph output as one snippet per source file, in the order the files first appear (the most relevant first), at
    // most as many as the Drafter reads. Each holds that file's NODE lines with their source excerpts; the header and the
    // EDGE lines go with the first. Per-file snippets let the verifier keep the files that answer and drop the rest, and
    // let the indirect-injection screen drop one file instead of the whole code context. Output with no NODE lines is
    // kept as it is.
    public static List<string> SplitByFile(string output)
    {
        var lead = new List<string>();
        var edges = new List<string>();
        var files = new List<(string File, List<string> Lines)>();
        List<string>? current = null;
        foreach (var line in output.Split('\n'))
        {
            var node = NodeFile().Match(line);
            if (node.Success)
            {
                var file = node.Groups["file"].Value;
                var index = files.FindIndex(f => f.File == file);
                if (index < 0)
                {
                    files.Add((file, []));
                    index = files.Count - 1;
                }
                current = files[index].Lines;
                current.Add(line);
            }
            else if (line.StartsWith(CodeGraphOutput.ExcerptLinePrefix, StringComparison.Ordinal) && current is not null)
            {
                current.Add(line);
            }
            else if (line.StartsWith("EDGE ", StringComparison.Ordinal))
            {
                edges.Add(line);
            }
            else if (files.Count == 0 && line.Length > 0)
            {
                lead.Add(line);
            }
        }

        if (files.Count == 0) return [output];

        var snippets = files.Take(DrafterAgent.MaxSnippetsPerSource).Select(f => string.Join('\n', f.Lines)).ToList();
        snippets[0] = string.Join('\n', lead.Append(snippets[0]).Concat(edges));
        return snippets;
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} intent={Intent}", Name, context.ProjectId, context.Intent);
        try
        {
            // U7: shared gate; see IntentRouting.
            if (!IntentRouting.NeedsCode(context))
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
                context.CodeSnippets.AddRange(SplitByFile(output));
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
