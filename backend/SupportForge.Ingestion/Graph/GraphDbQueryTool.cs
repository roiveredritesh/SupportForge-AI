using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using SupportForge.Agents.Tools;
using SupportForge.Ingestion.Code;

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
    internal const int MaxFulltextMatches = 10;
    private const int MaxTraversalSeeds = 5;
    internal const double RelevanceFloor = 0.65;

    // Test code is fetched too and then set aside unless the question is about tests: in a large repository it outnumbers
    // real code (django/django: 73% of definitions), so ranked together its matches fill every slot before the real one.
    // The candidate pool is wider than MaxFulltextMatches for that reason.
    private const int MaxCandidateMatches = 60;

    // Source excerpts are shown for this many matched definitions, best first; the rest are NODE lines only.
    private const int MaxExcerptedNodes = 3;

    // At most this many search terms reach Lucene, so a long, rambling question doesn't dilute the ones that matter.
    private const int MaxSearchTerms = 12;

    // V4 drops label and adds bodyTerms. label double-counted every one-word name: "values" matched as a label and again
    // as nameTerms, while "SecurityMiddleware" is a single label token and matched only once, so generic members
    // outranked the classes questions ask about. nameTerms carries the whole identifier as well as its words, so nothing
    // is lost. The rename-not-drop history is in GraphImportJob.
    internal const string SearchIndexName = "graphNodeSearchV4";
    internal const string CreateSearchIndexCypher =
        "CREATE FULLTEXT INDEX graphNodeSearchV4 IF NOT EXISTS FOR (n:GraphNode) ON EACH [n.nameTerms, n.summary, n.purpose, n.domainTerms, n.bodyTerms]";

    // Body terms are what the code uses, not what it is for, so a match there counts for less than one in a name or
    // summary. They decide between otherwise-equal candidates and find undocumented code.
    internal const double BodyTermsBoost = 0.4;

    // Words that carry no meaning for a code search, including the ones every code question uses ("which class ...").
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being", "of", "to", "in", "on", "for", "with", "by",
        "at", "from", "as", "and", "or", "not", "no", "it", "its", "this", "that", "these", "those", "which", "what",
        "who", "whom", "whose", "when", "where", "why", "how", "does", "do", "did", "can", "could", "should", "would",
        "will", "shall", "may", "might", "must", "there", "their", "they", "them", "we", "our", "you", "your", "i", "me",
        "my", "he", "she", "his", "her", "about", "into", "than", "then", "so", "such", "just", "also", "any", "some",
        "all", "each", "every", "more", "most", "other", "only", "own", "same", "very", "instead", "if", "else", "get",
        "class", "classes", "function", "functions", "method", "methods", "code", "file", "files", "implement",
        "implements", "implemented", "implementation", "project", "repo", "repository", "source", "use", "used", "uses",
        "using", "call", "calls", "called", "handle", "handles", "handled", "work", "works", "happen", "happens", "related",
        "terms", "documentation", "please", "tell", "show", "explain", "find",
    };

    private static readonly Regex QuestionWord = new(@"[A-Za-z][A-Za-z0-9_]*|\d+", RegexOptions.Compiled);

    private static readonly Regex TestQuestion = new(
        @"\b(test|tests|tested|testing|unit\s+test|spec|specs|fixture|fixtures|mock|mocks)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    // A graph imported before V4 existed has no V4 index yet, and querying a missing index throws. It is created on first
    // use (IF NOT EXISTS), so code search keeps working until the next import rewrites the graph. Until then, nodes
    // imported without nameTerms (before V3) or bodyTerms are matched only on their other fields.
    private volatile bool _searchIndexEnsured;
    private readonly CodeSearchTuning _tuning;

    public GraphDbQueryTool(IDriver driver, string database, ILogger<GraphDbQueryTool>? logger = null, CodeSearchTuning? tuning = null)
    {
        _driver = driver;
        _database = database;
        _logger = logger ?? NullLogger<GraphDbQueryTool>.Instance;
        _tuning = tuning ?? CodeSearchTuning.Default;
    }

    public async Task<string?> QueryAsync(string projectId, string question, bool retrying, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        var depth = retrying ? RetryDepth : DefaultDepth;
        var budget = retrying ? RetryBudget : DefaultBudget;
        var traversalLabel = retrying ? "DFS" : "BFS";

        var searchQuery = BuildSearchQuery(question);
        if (searchQuery.Length == 0) return null;

        if (!_searchIndexEnsured)
        {
            await session.ExecuteWriteAsync(tx => tx.RunAsync(CreateSearchIndexCypher));
            var awaitIndexes = await session.RunAsync("CALL db.awaitIndexes(300)");
            await awaitIndexes.ConsumeAsync();
            _searchIndexEnsured = true;
        }

        var matchRecords = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"""
                CALL db.index.fulltext.queryNodes('{SearchIndexName}', $question) YIELD node, score
                WHERE node.projectId = $projectId
                RETURN node, score
                ORDER BY score DESC
                LIMIT $limit
                """,
                new { question = FieldedQuery(searchQuery, _tuning.BodyTermsBoost), projectId, limit = MaxCandidateMatches });
            return await cursor.ToListAsync();
        });

        if (matchRecords.Count == 0)
        {
            _logger.LogInformation("CodeSearch terms={TermCount} candidates=0", searchQuery.Split(' ').Length);
            return null;
        }

        var includeTests = AsksAboutTests(question);
        var candidates = matchRecords
            .Select(r => (Node: r["node"].As<INode>(), Score: r["score"].As<double>()))
            .ToList();
        var ranked = includeTests ? candidates : candidates.Where(c => !IsTestNode(c.Node)).ToList();
        // Only test code matched: that is still better than no answer at all.
        if (ranked.Count == 0) ranked = candidates;
        ranked = ranked.Take(_tuning.MaxMatches).ToList();

        var topScore = ranked[0].Score;
        var relevantMatches = ranked
            .Where(r => ClearsRelevanceFloor(r.Score, topScore, _tuning.RelevanceFloor))
            .ToList();
        // Counts and scores only, never the question: enough to see which floor a miss falls under.
        _logger.LogInformation(
            "CodeSearch terms={TermCount} candidates={Candidates} nonTest={NonTest} top={TopScore:F2} relevant={Relevant}",
            searchQuery.Split(' ').Length, candidates.Count, candidates.Count(c => !IsTestNode(c.Node)), topScore, relevantMatches.Count);

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
        if (!includeTests) traversalNodes = traversalNodes.Where(n => !IsTestNode(n)).ToList();

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
        var excerptsLeft = MaxExcerptedNodes;
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

            // The best few matched definitions also show their opening source lines, so the answer can say what the
            // code does. Nodes arrive relevance-ranked, so these are the strongest matches.
            if (excerptsLeft > 0 && n.Properties.GetValueOrDefault("excerpt") is string excerpt && excerpt.Length > 0)
            {
                excerptsLeft--;
                foreach (var line in excerpt.Split('\n'))
                    sb.Append(CodeGraphOutput.ExcerptLinePrefix).Append(line).Append('\n');
            }

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

    // A node counts as test code by its isTest property, or by its path for a graph imported before isTest existed.
    private static bool IsTestNode(INode node) =>
        node.Properties.GetValueOrDefault("isTest") is bool isTest
            ? isTest
            : node.Properties.GetValueOrDefault("sourceFile") is string sourceFile && CodeGraphExtractor.IsTestPath(sourceFile);

    internal static bool AsksAboutTests(string question) => TestQuestion.IsMatch(question);

    // The Lucene query for a question: its meaningful words rather than the whole sentence. An identifier the customer typed
    // ("CsrfViewMiddleware", "get_object") is kept whole and also split into words, matching the nameTerms index field.
    // A longer word also gets a crude-stem prefix term ("streams" -> stream*), so "streams" finds "StreamingHttpResponse".
    // Each term is escaped. Returns "" when nothing meaningful is left.
    internal static string BuildSearchQuery(string question)
    {
        var terms = new List<string>();
        foreach (Match match in QuestionWord.Matches(question))
        {
            var word = match.Value;
            var lower = word.ToLowerInvariant();
            var parts = CodeGraphExtractor.IdentifierTerms(word).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && !StopWords.Contains(lower)) terms.Add(lower);
            foreach (var part in parts)
            {
                if (part.Length < 2 || StopWords.Contains(part)) continue;
                terms.Add(part);
                var stem = Stem(part);
                if (stem != part) terms.Add(stem + "*");
            }
        }

        return string.Join(' ', terms.Distinct(StringComparer.Ordinal).Take(MaxSearchTerms)
            .Select(t => t.EndsWith('*') ? EscapeLuceneQuery(t[..^1]) + "*" : EscapeLuceneQuery(t)));
    }

    // The search terms applied to every indexed field, with body terms weighted down (see BodyTermsBoost).
    internal static string FieldedQuery(string terms, double bodyTermsBoost) =>
        $"nameTerms:({terms}) summary:({terms}) purpose:({terms}) domainTerms:({terms}) "
        + $"bodyTerms:({terms})^{bodyTermsBoost.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // Strips one common English suffix, keeping at least four letters, so the result works as a prefix query.
    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 4
                && !(suffix == "s" && word.EndsWith("ss", StringComparison.Ordinal)))
                return word[..^suffix.Length];
        }
        return word;
    }

    // ponytail: chars/4 token estimate -- exact tokenizer parity isn't required, this only needs to
    // stop growth in the right ballpark.
    private static int EstimatedTokens(StringBuilder sb) => sb.Length / 4;

    // A match must clear both floors: relative (close enough to the top match) AND absolute (a
    // genuinely strong Lucene score on its own). Relative-only lets an all-noise result set pass
    // itself off as relevant, since the top match is always >= itself; see MinimumAbsoluteScore.
    internal static bool ClearsRelevanceFloor(double score, double topScore, double relevanceFloor = RelevanceFloor) =>
        score >= topScore * relevanceFloor && score >= MinimumAbsoluteScore;

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

/// <summary>
/// The ranking knobs of <see cref="GraphDbQueryTool"/>. Production uses <see cref="Default"/>; the code-retrieval eval
/// passes other values to compare them on real repositories.
/// </summary>
public sealed record CodeSearchTuning(double BodyTermsBoost, double RelevanceFloor, int MaxMatches)
{
    public static CodeSearchTuning Default { get; } =
        new(GraphDbQueryTool.BodyTermsBoost, GraphDbQueryTool.RelevanceFloor, GraphDbQueryTool.MaxFulltextMatches);
}
