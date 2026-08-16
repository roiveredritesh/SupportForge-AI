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
        // own transaction before the node/edge MERGEs below. Indexed on summary as well as label so
        // the start-node search in GraphDbQueryTool can match a question against captured doc-comment
        // prose ("why"), not just identifier names ("what") -- named graphNodeSearch, not the old
        // graphNodeLabel, since changing an existing index's ON EACH fields requires DROP+CREATE and
        // "IF NOT EXISTS" would otherwise silently keep pre-existing deployments on the label-only index.
        await session.ExecuteWriteAsync(tx => tx.RunAsync(
            "CREATE FULLTEXT INDEX graphNodeSearch IF NOT EXISTS FOR (n:GraphNode) ON EACH [n.label, n.summary]"));

        await session.ExecuteWriteAsync(async tx =>
        {
            // UNWIND-batched MERGE, not one query per node/edge: a repo's graph can be thousands of
            // nodes, and per-node round-trips would dominate import time.
            // U22: repo is part of the MERGE key, not just a SET property -- two repos in the same
            // project that happen to share a relative path (e.g. both have "src/index.ts") used to
            // collide into one GraphNode, silently mixing one repo's summary/edges into the other's.
            await tx.RunAsync(
                """
                UNWIND $nodes AS node
                MERGE (n:GraphNode {id: node.id, projectId: $projectId, repo: $repo})
                SET n.label = node.label,
                    n.fileType = node.fileType,
                    n.sourceFile = node.sourceFile,
                    n.sourceLocation = node.sourceLocation,
                    n.summary = node.summary,
                    n.shape = node.shape,
                    n.coverageLevel = node.coverageLevel
                """,
                new
                {
                    projectId = ProjectId,
                    repo = _repo,
                    // Neo4j.Driver maps anonymous-type properties to Cypher parameters by their exact
                    // C# name (case-sensitive) -- every property must be explicitly lower-cased here to
                    // match the lowercase field names ("node.id", "node.label", ...) referenced in the
                    // Cypher above, or that field silently binds to null in the query instead of erroring.
                    // shape/coverageLevel (U5, Tier 0/1 of the code-graph classification plan) are
                    // purely additive here -- this unit only carries them through to Neo4j, it does not
                    // yet change which nodes get written (that's the hard-delete policy step, U9).
                    nodes = graph.Nodes.Select(n => new
                    {
                        id = n.Id,
                        label = n.Label,
                        fileType = n.FileType,
                        sourceFile = n.SourceFile,
                        sourceLocation = n.SourceLocation,
                        summary = n.Summary,
                        shape = n.Shape,
                        coverageLevel = n.CoverageLevel,
                    }),
                });

            // "defines"/"imports" edges only ever connect nodes CodeGraphExtractor found while
            // walking this same repo, so both ends are scoped to $repo -- same fix as the node MERGE
            // above, needed so an edge can't bridge two different repos' same-path nodes now that
            // they're distinct GraphNodes.
            await tx.RunAsync(
                """
                UNWIND $edges AS edge
                MATCH (a:GraphNode {id: edge.source, projectId: $projectId, repo: $repo})
                MATCH (b:GraphNode {id: edge.target, projectId: $projectId, repo: $repo})
                MERGE (a)-[r:EDGE {relation: edge.relation}]->(b)
                SET r.confidence = edge.confidence
                """,
                new
                {
                    projectId = ProjectId,
                    repo = _repo,
                    edges = graph.Edges.Where(e => e.Relation != "calls_endpoint").Select(e => new
                    {
                        source = e.Source,
                        target = e.Target,
                        relation = e.Relation,
                        confidence = e.Confidence,
                    }),
                });

            // U23: "calls_endpoint" is the one cross-repo edge type -- the caller file lives in this
            // job's repo ($repo), but the endpoint it targets may have been defined by a different
            // repo's import (or none at all, if that repo hasn't been ingested yet / the route guess
            // doesn't match anything real). So only the source end is repo-scoped; the target MATCH
            // deliberately spans every repo in the project so blast-radius traversal can bridge them.
            // A target that doesn't exist anywhere just means the MATCH finds nothing and no edge is
            // written -- same "silently skip" behavior AddImportEdges already has for unresolved
            // same-repo imports.
            await tx.RunAsync(
                """
                UNWIND $edges AS edge
                MATCH (a:GraphNode {id: edge.source, projectId: $projectId, repo: $repo})
                MATCH (b:GraphNode {id: edge.target, projectId: $projectId})
                MERGE (a)-[r:EDGE {relation: edge.relation}]->(b)
                SET r.confidence = edge.confidence
                """,
                new
                {
                    projectId = ProjectId,
                    repo = _repo,
                    edges = graph.Edges.Where(e => e.Relation == "calls_endpoint").Select(e => new
                    {
                        source = e.Source,
                        target = e.Target,
                        relation = e.Relation,
                        confidence = e.Confidence,
                    }),
                });
        });
    }
}
