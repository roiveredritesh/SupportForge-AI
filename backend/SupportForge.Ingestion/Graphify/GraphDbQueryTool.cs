using System.Text;
using Neo4j.Driver;
using SupportForge.Agents.Tools;

namespace SupportForge.Ingestion.Graphify;

/// <summary>
/// WS1 (retrieval-pipeline remediation plan) replacement for <see cref="GraphifyQueryTool"/>: same
/// <see cref="IGraphifyQueryTool"/> contract, but queries the graph database (populated by
/// <see cref="GraphImportJob"/>) via a pooled driver instead of shelling out to <c>graphify query</c> per
/// call. Output is formatted to match <c>graphify query</c>'s plain-text shape ("NODE ... " / "EDGE ...
/// --relation--> ..." lines) so <see cref="Agents.CodeAnalyzerVerifier"/> and <see cref="Agents.DrafterAgent"/>
/// need no changes.
///
/// NOT wired into DI as the active <see cref="IGraphifyQueryTool"/> yet -- see the plan's WS1 risk note:
/// `graphify query`'s exact entry-node matching algorithm is internal to the binary and unverified here.
/// This full-text-index-based entry match plus depth-bounded traversal is a best-effort approximation, not
/// a proven behavioral match. Cut over only after the parity spike (Program.cs's DI registration change)
/// compares this against <see cref="GraphifyQueryTool"/> on real questions.
/// </summary>
public sealed class GraphDbQueryTool : IGraphifyQueryTool
{
    private const int DefaultBudget = 2000;
    private const int RetryBudget = 4000;
    private const int DefaultDepth = 2;
    private const int RetryDepth = 4;
    private const int MaxStartNodes = 3;

    private readonly IDriver _driver;
    private readonly string _database;

    public GraphDbQueryTool(IDriver driver, string database)
    {
        _driver = driver;
        _database = database;
    }

    public async Task<string?> QueryAsync(string projectId, string question, bool retrying, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        var depth = retrying ? RetryDepth : DefaultDepth;
        var budget = retrying ? RetryBudget : DefaultBudget;
        var traversalLabel = retrying ? "DFS" : "BFS";

        var startRecords = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                CALL db.index.fulltext.queryNodes('graphNodeLabel', $question) YIELD node, score
                WHERE node.projectId = $projectId
                RETURN node.id AS id, node.label AS label
                ORDER BY score DESC
                LIMIT $limit
                """,
                new { question, projectId, limit = MaxStartNodes });
            return await cursor.ToListAsync();
        });
        var starts = startRecords.Select(r => (Id: r["id"].As<string>(), Label: r["label"].As<string>())).ToList();

        if (starts.Count == 0) return null;

        var traversalRecords = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (start:GraphNode)
                WHERE start.id IN $startIds AND start.projectId = $projectId
                MATCH p = (start)-[*0..%DEPTH%]-(other:GraphNode {projectId: $projectId})
                UNWIND nodes(p) AS n
                WITH collect(DISTINCT n) AS ns, collect(DISTINCT relationships(p)) AS relLists
                UNWIND relLists AS rl
                UNWIND rl AS r
                RETURN ns, collect(DISTINCT r) AS rs
                """.Replace("%DEPTH%", depth.ToString()),
                new { startIds = starts.Select(s => s.Id).ToArray(), projectId });
            return await cursor.ToListAsync();
        });

        List<INode> nodes = [];
        List<IRelationship> edges = [];
        if (traversalRecords.Count > 0)
        {
            nodes = traversalRecords[0]["ns"].As<List<INode>>();
            edges = traversalRecords[0]["rs"].As<List<IRelationship>>();
        }

        if (nodes.Count == 0) return null;

        return FormatAndTruncate(traversalLabel, depth, starts.Select(s => s.Label), nodes, edges, budget);
    }

    // Mirrors `graphify query`'s plain-text output shape (verified against a real run of the CLI) so
    // downstream prompts/parsing built around that shape need no changes.
    private static string FormatAndTruncate(
        string traversalLabel, int depth, IEnumerable<string> startLabels,
        IReadOnlyList<INode> nodes, IReadOnlyList<IRelationship> edges, int budget)
    {
        var byElementId = nodes.ToDictionary(n => n.ElementId, n => n);
        var sb = new StringBuilder();
        sb.Append("Traversal: ").Append(traversalLabel).Append(" depth=").Append(depth)
          .Append(" | Start: [").Append(string.Join(", ", startLabels.Select(l => $"'{l}'"))).Append(']')
          .Append(" | ").Append(nodes.Count).Append(" nodes found").Append('\n').Append('\n');

        foreach (var n in nodes)
        {
            sb.Append("NODE ").Append(n.Properties.GetValueOrDefault("label"))
              .Append(" [src=").Append(n.Properties.GetValueOrDefault("sourceFile"))
              .Append(" loc=").Append(n.Properties.GetValueOrDefault("sourceLocation"))
              .Append(" community=]").Append('\n');

            if (EstimatedTokens(sb) >= budget) return sb.ToString();
        }

        foreach (var e in edges)
        {
            if (!byElementId.TryGetValue(e.StartNodeElementId, out var start) ||
                !byElementId.TryGetValue(e.EndNodeElementId, out var end))
                continue;

            sb.Append("EDGE ").Append(start.Properties.GetValueOrDefault("label"))
              .Append(" --").Append(e.Properties.GetValueOrDefault("relation"))
              .Append(" [").Append(e.Properties.GetValueOrDefault("confidence")).Append("]--> ")
              .Append(end.Properties.GetValueOrDefault("label")).Append('\n');

            if (EstimatedTokens(sb) >= budget) return sb.ToString();
        }

        return sb.ToString();
    }

    // ponytail: chars/4 token estimate, same rough heuristic class as graphify's own --budget --
    // exact tokenizer parity isn't required, this only needs to stop growth in the right ballpark.
    private static int EstimatedTokens(StringBuilder sb) => sb.Length / 4;
}
