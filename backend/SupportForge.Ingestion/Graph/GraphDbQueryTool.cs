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

    // The code graph's extractor only ever emits "defines" edges (file -> symbol), so traversal
    // can never bridge two files -- e.g. eight sibling grader types each living in their own file
    // are graph-unreachable from one another no matter the depth. Given that, the fulltext index is
    // the real retrieval mechanism here, not a seed-picker for traversal: pull a wide set of matches
    // (MaxFulltextMatches) and surface every one that's actually relevant (RelevanceFloor, relative
    // to the top score) directly in the output, rather than gambling on 3 seeds and hoping traversal
    // finds the rest. Traversal is kept only for same-file context around the best few matches.
    // ponytail: on a real repo, per-term Lucene scoring puts plenty of tangentially-word-overlapping
    // matches within half the top score (observed: an unrelated session-teardown function outscored
    // the actually-relevant node for a "lifecycle hooks" question) -- 0.65/10 trades a little recall
    // for a lot less noise reaching the small drafting model. Revisit with a smarter query (term
    // extraction, stopword stripping) if recall turns out to matter more than this signal-to-noise cut.
    private const int MaxFulltextMatches = 10;
    private const int MaxTraversalSeeds = 5;
    private const double RelevanceFloor = 0.65;

    // ponytail: RelevanceFloor alone is relative to topScore, so a question whose only matches are
    // all noise still has its own noise clear the floor -- observed live against project 001's
    // (still vendor-polluted, pre-Phase-1-cleanup) graph: a totally unrelated query ("vehicle insert
    // null exception") scored 1.69-1.94 on pure incidental word overlap with generic vendored
    // function names (prism.js's matchPattern/parseRange), and this file's own originally-reported
    // incident query ("enable disable") scored 3.03 against rater-js's enable/disable. 3.2 sits just
    // above that incident's actual noise ceiling. This is a starting value with no genuine-match data
    // to calibrate against yet (nothing in this graph matched "vehicle" at all) -- revisit once
    // Phase 1-3 cleanup lets a real business-relevant match's score be observed and compared.
    private const double MinimumAbsoluteScore = 3.2;

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

        var matchRecords = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                CALL db.index.fulltext.queryNodes('graphNodeSearchV2', $question) YIELD node, score
                WHERE node.projectId = $projectId
                RETURN node, score
                ORDER BY score DESC
                LIMIT $limit
                """,
                new { question = EscapeLuceneQuery(question), projectId, limit = MaxFulltextMatches });
            return await cursor.ToListAsync();
        });

        if (matchRecords.Count == 0) return null;

        var topScore = matchRecords[0]["score"].As<double>();
        var relevantMatches = matchRecords
            .Select(r => (Node: r["node"].As<INode>(), Score: r["score"].As<double>()))
            .Where(r => ClearsRelevanceFloor(r.Score, topScore))
            .ToList();

        // Both floors passed and nothing survived (e.g. the top match itself is below the absolute
        // floor) -- "no relevant code found" rather than surfacing the best-scoring noise.
        if (relevantMatches.Count == 0) return null;

        var seedIds = relevantMatches.Take(MaxTraversalSeeds)
            .Select(r => r.Node.Properties["id"].As<string>()).ToArray();

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
                new { startIds = seedIds, projectId });
            return await cursor.ToListAsync();
        });

        List<INode> traversalNodes = [];
        List<IRelationship> edges = [];
        if (traversalRecords.Count > 0)
        {
            traversalNodes = traversalRecords[0]["ns"].As<List<INode>>();
            edges = traversalRecords[0]["rs"].As<List<IRelationship>>();
        }

        // Relevance-ranked matches first (guarantees the actual answer survives budget truncation),
        // then whatever same-file context traversal turned up that isn't already in that list.
        var seenIds = new HashSet<string>();
        List<INode> nodes = [];
        foreach (var m in relevantMatches)
            if (seenIds.Add(m.Node.ElementId)) nodes.Add(m.Node);
        foreach (var n in traversalNodes)
            if (seenIds.Add(n.ElementId)) nodes.Add(n);

        if (nodes.Count == 0) return null;

        return FormatAndTruncate(traversalLabel, depth, relevantMatches.Select(m => m.Node.Properties.GetValueOrDefault("label")?.ToString() ?? ""), nodes, edges, budget);
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
              .Append(" community=]");

            // Doc-comment prose captured by CodeGraphExtractor (see GraphImportModels.CodeGraphNode.Summary)
            // -- without this, every NODE line is identifier skeleton only, and CodeAnalyzerAgent/DrafterAgent
            // have nothing but the label/path to reason from for "why"-shaped questions.
            if (n.Properties.GetValueOrDefault("summary") is string summary && summary.Length > 0)
                sb.Append(": ").Append(summary);

            sb.Append('\n');

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

    // A match must clear both floors: relative (close enough to the top match) AND absolute (a
    // genuinely strong Lucene score on its own). Relative-only lets an all-noise result set pass
    // itself off as relevant, since the top match is always >= itself; see MinimumAbsoluteScore.
    internal static bool ClearsRelevanceFloor(double score, double topScore) =>
        score >= topScore * RelevanceFloor && score >= MinimumAbsoluteScore;

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
