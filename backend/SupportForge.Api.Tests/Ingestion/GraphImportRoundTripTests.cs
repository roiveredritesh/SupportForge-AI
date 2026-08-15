using System.Text.Json;
using Neo4j.Driver;
using SupportForge.Ingestion.Graph;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

/// <summary>
/// Round-trips a small graph through a real Neo4j instance: GraphImportJob writes it, then a raw read
/// verifies the node/edge properties actually landed. Regression test for a real bug found by running
/// the pipeline against a live Neo4j -- Neo4j.Driver maps anonymous-type properties to Cypher parameters
/// by their exact (case-sensitive) C# name, so `n.Id`/`n.Label`/`e.Source`/`e.Target`/`e.Relation`/
/// `e.Confidence` silently bound to null instead of the lowercase `node.id`/`node.label`/`edge.source`/...
/// the Cypher referenced. No mock catches this class of bug -- only a live driver round-trip does.
/// </summary>
public class GraphImportRoundTripTests
{
    private const string BoltUri = "bolt://localhost:7687";

    private static async Task<IDriver?> TryConnectAsync()
    {
        var driver = GraphDatabase.Driver(BoltUri, AuthTokens.Basic("neo4j", "supportforge-test"));
        try
        {
            await driver.VerifyConnectivityAsync();
            return driver;
        }
        catch
        {
            await driver.DisposeAsync();
            return null;
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_WritesNodeAndEdgeProperties_ThatSurviveARealNeo4jRoundTrip()
    {
        var driver = await TryConnectAsync();
        Skip.If(driver is null, $"requires a live Neo4j at {BoltUri}");

        var projectId = "graph-import-roundtrip-" + Guid.NewGuid();
        try
        {
            var graph = new CodeGraphFile
            {
                Nodes =
                [
                    new CodeGraphNode { Id = "a.cs", Label = "a.cs", FileType = "csharp", SourceFile = "a.cs", SourceLocation = "L1", Summary = "Handles session auth." },
                    new CodeGraphNode { Id = "a.cs::Auth", Label = "Auth", FileType = "csharp", SourceFile = "a.cs", SourceLocation = "L3" },
                ],
                Edges = [new CodeGraphEdge { Source = "a.cs", Target = "a.cs::Auth", Relation = "defines", Confidence = "high" }],
            };
            var graphJsonPath = Path.Combine(Path.GetTempPath(), $"{projectId}.json");
            await File.WriteAllTextAsync(graphJsonPath, JsonSerializer.Serialize(graph));

            var importJob = new GraphImportJob(projectId, graphJsonPath, "repo1", driver!, "neo4j");
            await importJob.RunAsync(CancellationToken.None);

            await using var session = driver!.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (n:GraphNode {projectId: $projectId}) RETURN n.id AS id, n.label AS label, n.summary AS summary ORDER BY n.id",
                    new { projectId });
                return await cursor.ToListAsync();
            });

            Assert.Equal(2, records.Count);
            Assert.Contains(records, r => r["id"].As<string>() == "a.cs" && r["label"].As<string>() == "a.cs" && r["summary"].As<string>() == "Handles session auth.");
            Assert.Contains(records, r => r["id"].As<string>() == "a.cs::Auth" && r["label"].As<string>() == "Auth");

            var edgeRecords = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    """
                    MATCH (a:GraphNode {projectId: $projectId})-[r:EDGE]->(b:GraphNode {projectId: $projectId})
                    RETURN r.relation AS relation, r.confidence AS confidence
                    """,
                    new { projectId });
                return await cursor.ToListAsync();
            });

            Assert.Single(edgeRecords);
            Assert.Equal("defines", edgeRecords[0]["relation"].As<string>());
            Assert.Equal("high", edgeRecords[0]["confidence"].As<string>());
        }
        finally
        {
            if (driver is not null)
            {
                await using var cleanupSession = driver.AsyncSession();
                await cleanupSession.ExecuteWriteAsync(tx => tx.RunAsync(
                    "MATCH (n:GraphNode {projectId: $projectId}) DETACH DELETE n", new { projectId }));
                await driver.DisposeAsync();
            }
        }
    }

    // U22 regression test: two repos in the same project each have a file at the same relative path
    // ("src/index.ts"). Before the fix, GraphImportJob's MERGE key was {id, projectId} only, so the
    // second repo's import silently collided into the first repo's node instead of creating its own.
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_KeepsRepoNodesDistinct_WhenTwoReposShareARelativePath()
    {
        var driver = await TryConnectAsync();
        Skip.If(driver is null, $"requires a live Neo4j at {BoltUri}");

        var projectId = "graph-import-collision-" + Guid.NewGuid();
        try
        {
            CodeGraphFile GraphFor(string summary) => new()
            {
                Nodes = [new CodeGraphNode { Id = "src/index.ts", Label = "index.ts", FileType = "typescript", SourceFile = "src/index.ts", SourceLocation = "L1", Summary = summary }],
            };

            var repo1JsonPath = Path.Combine(Path.GetTempPath(), $"{projectId}-repo1.json");
            var repo2JsonPath = Path.Combine(Path.GetTempPath(), $"{projectId}-repo2.json");
            await File.WriteAllTextAsync(repo1JsonPath, JsonSerializer.Serialize(GraphFor("repo1's index file.")));
            await File.WriteAllTextAsync(repo2JsonPath, JsonSerializer.Serialize(GraphFor("repo2's index file.")));

            await new GraphImportJob(projectId, repo1JsonPath, "repo1", driver!, "neo4j").RunAsync(CancellationToken.None);
            await new GraphImportJob(projectId, repo2JsonPath, "repo2", driver!, "neo4j").RunAsync(CancellationToken.None);

            await using var session = driver!.AsyncSession();
            var records = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (n:GraphNode {projectId: $projectId, id: 'src/index.ts'}) RETURN n.repo AS repo, n.summary AS summary ORDER BY n.repo",
                    new { projectId });
                return await cursor.ToListAsync();
            });

            Assert.Equal(2, records.Count); // not collided into one node
            Assert.Contains(records, r => r["repo"].As<string>() == "repo1" && r["summary"].As<string>() == "repo1's index file.");
            Assert.Contains(records, r => r["repo"].As<string>() == "repo2" && r["summary"].As<string>() == "repo2's index file.");
        }
        finally
        {
            if (driver is not null)
            {
                await using var cleanupSession = driver.AsyncSession();
                await cleanupSession.ExecuteWriteAsync(tx => tx.RunAsync(
                    "MATCH (n:GraphNode {projectId: $projectId}) DETACH DELETE n", new { projectId }));
                await driver.DisposeAsync();
            }
        }
    }
}
