using System.Text.Json;
using Neo4j.Driver;

namespace SupportForge.Ingestion.Graphify;

/// <summary>
/// WS1 (retrieval-pipeline remediation plan): reads a repo's <c>graphify-out/graph.json</c> (written by
/// <c>graphify extract --no-cluster</c>, unchanged) and imports it into the graph database, tagging every
/// node with <c>projectId</c>/<c>repo</c>. Runs per repo, same as <see cref="Code.CodeIngestionJob"/>'s
/// extract step -- unlike <see cref="Code.CodeGraphMergeJob"/>, no separate merge step is needed, because
/// <see cref="GraphDbQueryTool"/> scopes every query by the <c>projectId</c> property directly.
/// </summary>
public sealed class GraphImportJob : IIngestionJob
{
    private readonly string _graphJsonPath;
    private readonly string _repo;
    private readonly IDriver _driver;
    private readonly string _database;

    public string ProjectId { get; }

    public GraphImportJob(string projectId, string graphJsonPath, string repo, IDriver driver, string database)
    {
        ProjectId = projectId;
        _graphJsonPath = graphJsonPath;
        _repo = repo;
        _driver = driver;
        _database = database;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!File.Exists(_graphJsonPath)) return; // extract hasn't produced a graph yet -- graceful, not an error

        var json = await File.ReadAllTextAsync(_graphJsonPath, ct);
        var graph = JsonSerializer.Deserialize<GraphifyGraphFile>(json)
            ?? throw new InvalidOperationException($"'{_graphJsonPath}' did not deserialize to a graphify graph.");

        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "CREATE FULLTEXT INDEX graphNodeLabel IF NOT EXISTS FOR (n:GraphNode) ON EACH [n.label]");

            // UNWIND-batched MERGE, not one query per node/edge: a repo's graph can be thousands of
            // nodes, and per-node round-trips would dominate import time.
            await tx.RunAsync(
                """
                UNWIND $nodes AS node
                MERGE (n:GraphNode {id: node.id, projectId: $projectId})
                SET n.label = node.label,
                    n.fileType = node.fileType,
                    n.sourceFile = node.sourceFile,
                    n.sourceLocation = node.sourceLocation,
                    n.repo = $repo
                """,
                new
                {
                    projectId = ProjectId,
                    repo = _repo,
                    nodes = graph.Nodes.Select(n => new
                    {
                        n.Id,
                        n.Label,
                        fileType = n.FileType,
                        sourceFile = n.SourceFile,
                        sourceLocation = n.SourceLocation,
                    }),
                });

            await tx.RunAsync(
                """
                UNWIND $edges AS edge
                MATCH (a:GraphNode {id: edge.source, projectId: $projectId})
                MATCH (b:GraphNode {id: edge.target, projectId: $projectId})
                MERGE (a)-[r:EDGE {relation: edge.relation}]->(b)
                SET r.confidence = edge.confidence
                """,
                new
                {
                    projectId = ProjectId,
                    edges = graph.Edges.Select(e => new { e.Source, e.Target, e.Relation, e.Confidence }),
                });
        });
    }
}
