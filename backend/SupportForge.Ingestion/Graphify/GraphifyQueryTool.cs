using SupportForge.Agents.Tools;

namespace SupportForge.Ingestion.Graphify;

public sealed class GraphifyQueryTool : IGraphifyQueryTool
{
    private const string NoMatch = "No matching nodes found.";

    private readonly GraphifyCliRunner _graphify;
    private readonly string _repoCacheRoot;

    public GraphifyQueryTool(GraphifyCliRunner graphify, string repoCacheRoot)
    {
        _graphify = graphify;
        _repoCacheRoot = repoCacheRoot;
    }

    public async Task<string?> QueryAsync(string projectId, string question, bool retrying, CancellationToken ct = default)
    {
        var graphPath = Path.Combine(_repoCacheRoot, projectId, "graphify-project", "graph.json");
        if (!File.Exists(graphPath)) return null; // no repos ingested yet -- graceful, not an error

        var args = new List<string> { "query", question, "--graph", graphPath };
        // Retry gets a genuinely different query, not a rerun of an identical one: a plain BFS
        // rerun on retry would deterministically return the same output and waste an LLM judge
        // call before failing final.
        args.AddRange(retrying ? ["--dfs", "--budget", "4000"] : ["--budget", "2000"]);

        var output = await _graphify.RunAsync(Directory.GetCurrentDirectory(), environment: null, ct, args.ToArray());
        return string.IsNullOrWhiteSpace(output) || output.Trim() == NoMatch ? null : output;
    }
}
