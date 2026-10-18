using System.Text.Json;
using System.Text.RegularExpressions;
using Neo4j.Driver;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Graph;

// Retrieval-only check of the code path on a real repository, with no LLM call: extracts the repository with the real
// CodeGraphExtractor, imports it with the real GraphImportJob and asks every question of a code-QA fixture
// (Scripts/e2e/code-qa/*.json) through the real GraphDbQueryTool. For each question it reports whether the expected
// definition (label and file) is among the NODE lines handed to the answering model, and at which position. Fast and
// free, so retrieval changes can be compared across languages before an LLM run.
//
// Usage: dotnet SupportForge.Evals.dll code-retrieval <fixture.json> <repo checkout dir>
internal static partial class CodeRetrievalEval
{
    private const string ProjectId = "code-retrieval-eval";

    [GeneratedRegex(@"^NODE (?<label>\S+) \[src=(?<src>\S+) ")]
    private static partial Regex NodeLine();

    public static async Task<int> RunAsync(string fixturePath, string repoDir, Neo4jOptions neo4jOptions)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var repo = fixture.RootElement.GetProperty("repo").GetString() ?? "repo";
        var questions = fixture.RootElement.GetProperty("questions").EnumerateArray().Select(q => (
            Q: q.GetProperty("q").GetString() ?? "",
            Answer: q.GetProperty("answer").GetString() ?? "",
            Path: q.GetProperty("path").GetString() ?? "",
            Kind: q.TryGetProperty("kind", out var k) ? k.GetString() ?? "-" : "-")).ToList();

        var authToken = string.IsNullOrEmpty(neo4jOptions.Password)
            ? AuthTokens.None
            : AuthTokens.Basic(neo4jOptions.User, neo4jOptions.Password);
        await using var driver = GraphDatabase.Driver(neo4jOptions.Uri, authToken);
        await driver.VerifyConnectivityAsync();

        var started = DateTime.UtcNow;
        var graph = CodeGraphExtractor.Extract(repoDir);
        var graphPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}-graph.json");
        await File.WriteAllTextAsync(graphPath, JsonSerializer.Serialize(graph));
        try
        {
            await new GraphImportJob(ProjectId, graphPath, repo, driver, neo4jOptions.Database).RunAsync(CancellationToken.None);
        }
        finally
        {
            File.Delete(graphPath);
        }

        Console.WriteLine($"## {repo}: {graph.Nodes.Count} nodes extracted and imported in {(DateTime.UtcNow - started).TotalSeconds:F0}s");
        Console.WriteLine();
        Console.WriteLine("| # | Kind | Answer | In context | Position | Retry in context | Top 3 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");

        var tool = new GraphDbQueryTool(driver, neo4jOptions.Database);
        var found = 0;
        var foundOnRetry = 0;
        var byKind = new SortedDictionary<string, (int Total, int Found)>(StringComparer.Ordinal);
        for (var i = 0; i < questions.Count; i++)
        {
            var (q, answer, path, kind) = questions[i];
            var nodes = Nodes(await tool.QueryAsync(ProjectId, q, retrying: false));
            var retryNodes = Nodes(await tool.QueryAsync(ProjectId, q, retrying: true));
            var position = nodes.FindIndex(n => n.Label == answer && n.Src == path) + 1;
            var retryHit = retryNodes.Any(n => n.Label == answer && n.Src == path);
            if (position > 0) found++;
            if (retryHit) foundOnRetry++;
            var (total, hits) = byKind.GetValueOrDefault(kind);
            byKind[kind] = (total + 1, hits + (position > 0 ? 1 : 0));
            var top = string.Join(", ", nodes.Take(3).Select(n => n.Label));
            Console.WriteLine($"| {i + 1} | {kind} | {answer} | {(position > 0 ? "yes" : "no")} | {(position > 0 ? position : "-")} | {(retryHit ? "yes" : "no")} | {top} |");
        }

        Console.WriteLine();
        Console.WriteLine($"**{repo}:** answer in context {found}/{questions.Count}, on retry {foundOnRetry}/{questions.Count}; by kind: "
            + string.Join("; ", byKind.Select(k => $"{k.Key} {k.Value.Found}/{k.Value.Total}")));
        Console.WriteLine();

        // CODE_SEARCH_SWEEP="boost:floor:max,..." also scores other ranking settings on the same import, one line each.
        foreach (var tuning in SweepSettings())
        {
            var sweepTool = new GraphDbQueryTool(driver, neo4jOptions.Database, tuning: tuning);
            var hitsAtTuning = 0;
            var positions = new List<int>();
            foreach (var (q, answer, path, _) in questions)
            {
                var position = Nodes(await sweepTool.QueryAsync(ProjectId, q, retrying: false))
                    .FindIndex(n => n.Label == answer && n.Src == path) + 1;
                if (position > 0) { hitsAtTuning++; positions.Add(position); }
            }
            Console.WriteLine($"SWEEP {repo} boost={tuning.BodyTermsBoost} floor={tuning.RelevanceFloor} max={tuning.MaxMatches}: "
                + $"{hitsAtTuning}/{questions.Count} in context, mean position {(positions.Count > 0 ? positions.Average() : 0):F1}");
        }

        await using var session = driver.AsyncSession(o => o.WithDatabase(neo4jOptions.Database));
        await session.ExecuteWriteAsync(tx => tx.RunAsync(
            "MATCH (n:GraphNode {projectId: $projectId}) DETACH DELETE n", new { projectId = ProjectId }));
        return 0;
    }

    private static IEnumerable<CodeSearchTuning> SweepSettings() =>
        (Environment.GetEnvironmentVariable("CODE_SEARCH_SWEEP") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Split(':'))
            .Select(p => new CodeSearchTuning(
                double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)));

    private static List<(string Label, string Src)> Nodes(string? output) =>
        output is null
            ? []
            : output.Split('\n')
                .Select(l => NodeLine().Match(l))
                .Where(m => m.Success)
                .Select(m => (m.Groups["label"].Value, m.Groups["src"].Value))
                .ToList();
}
