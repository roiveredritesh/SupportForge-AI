using System.Text;
using Neo4j.Driver;
using SupportForge.Agents.Tools;

namespace SupportForge.Ingestion.Graph;

/// <summary>
/// Queries the Neo4j-backed code graph (populated by <see cref="GraphImportJob"/>) via a pooled
/// driver: a full-text-index entry match followed by a depth-bounded traversal, formatted as
/// "NODE ... " / "EDGE ... --relation--> ..." lines.
/// </summary>
public sealed class GraphDbQueryTool : ICodeGraphQueryTool
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
                new { question = EscapeLuceneQuery(question), projectId, limit = MaxStartNodes });
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

    // ponytail: chars/4 token estimate -- exact tokenizer parity isn't required, this only needs to
    // stop growth in the right ballpark.
    private static int EstimatedTokens(StringBuilder sb) => sb.Length / 4;

    // $question reaches Neo4j as a Lucene query string, not a plain search string -- an unescaped
    // special char (a "/" in a file path, a stray quote, etc.) throws a Lucene TokenMgrError that
    // was previously unhandled all the way up to a raw 500. Escaping every Lucene special char
    // preserves fuzzy term matching while treating the question as literal text.
    private static readonly char[] LuceneSpecialChars =
        ['\\', '+', '-', '&', '|', '!', '(', ')', '{', '}', '[', ']', '^', '"', '~', '*', '?', ':', '/'];

    internal static string EscapeLuceneQuery(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (Array.IndexOf(LuceneSpecialChars, c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
