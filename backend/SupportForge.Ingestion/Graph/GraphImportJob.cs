using System.Text.Json;
using Neo4j.Driver;

namespace SupportForge.Ingestion.Graph;

/// <summary>
/// Reads a repo's extracted code graph JSON file and imports it into Neo4j, tagging every node with
/// <c>projectId</c>/<c>repo</c> so <see cref="GraphDbQueryTool"/> can scope every query to a project.
/// Runs per repo, same as <see cref="Code.CodeIngestionJob"/>.
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
        if (!File.Exists(_graphJsonPath)) return; // no code graph produced for this repo yet -- graceful, not an error

        var json = await File.ReadAllTextAsync(_graphJsonPath, ct);
        var graph = JsonSerializer.Deserialize<CodeGraphFile>(json)
            ?? throw new InvalidOperationException($"'{_graphJsonPath}' did not deserialize to a code graph.");

        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        // Neo4j rejects a data write in the same transaction as a schema modification ("Tried to
        // execute Write query after executing Schema modification"), so the index create needs its
        // own transaction before the node/edge MERGEs below.
        await session.ExecuteWriteAsync(tx => tx.RunAsync(
            "CREATE FULLTEXT INDEX graphNodeLabel IF NOT EXISTS FOR (n:GraphNode) ON EACH [n.label]"));

        await session.ExecuteWriteAsync(async tx =>
        {
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
