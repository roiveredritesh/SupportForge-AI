using System.Text.Json;
using Neo4j.Driver;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Graph;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

/// <summary>
/// Fixture-repo-pair test: two small repos ("api-repo" defining an endpoint, "widget-repo" calling
/// it via HttpClient) walked through the real pipeline -- CodeGraphExtractor.Extract, GraphImportJob
/// (twice, once per repo), then BlastRadiusQueryTool -- against a live Neo4j. Same live-driver-or-skip
/// pattern as GraphImportRoundTripTests, for the same reason: no mock catches a Cypher-level bug.
/// </summary>
public class BlastRadiusQueryToolTests
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

    private static string CreateRepoFixture(params (string RelativePath, string Content)[] files)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(repoDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
        return repoDir;
    }

    private static async Task ImportAsync(string projectId, string repoDir, string repoName, IDriver driver, CancellationToken ct)
    {
        var graph = CodeGraphExtractor.Extract(repoDir);
        var graphJsonPath = Path.Combine(Path.GetTempPath(), $"{projectId}-{repoName}.json");
        await File.WriteAllTextAsync(graphJsonPath, JsonSerializer.Serialize(graph), ct);
        await new GraphImportJob(projectId, graphJsonPath, repoName, driver, "neo4j").RunAsync(ct);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task QueryAsync_FindsCrossRepoCaller_WhenAnotherRepoCallsTheEndpoint()
    {
        var driver = await TryConnectAsync();
        Skip.If(driver is null, $"requires a live Neo4j at {BoltUri}");

        var projectId = "blast-radius-" + Guid.NewGuid();
        try
        {
            var apiRepoDir = CreateRepoFixture(("ChatController.cs",
                "namespace ApiRepo;\n\n" +
                "[Route(\"api/chat\")]\n" +
                "public partial class ChatController\n{\n" +
                "    [HttpPost(\"query\")]\n" +
                "    public void Query() { }\n" +
                "}\n"));
            var widgetRepoDir = CreateRepoFixture(("WidgetClient.cs",
                "namespace WidgetRepo;\n\n" +
                "public class WidgetClient\n{\n" +
                "    public async Task Fetch(HttpClient http) => await http.PostAsync(\"api/chat/query\", null);\n" +
                "}\n"));

            await ImportAsync(projectId, apiRepoDir, "api-repo", driver!, CancellationToken.None);
            await ImportAsync(projectId, widgetRepoDir, "widget-repo", driver!, CancellationToken.None);

            var tool = new BlastRadiusQueryTool(driver!, "neo4j");
            var result = await tool.QueryAsync(projectId, ["ChatController.cs"], CancellationToken.None);

            var entry = Assert.Single(result);
            Assert.Equal("api-repo", entry.Repo);
            Assert.Equal(["widget-repo"], entry.UsedBy);
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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task QueryAsync_ReturnsEmpty_WhenChangedFileHasNoCrossRepoCallers()
    {
        var driver = await TryConnectAsync();
        Skip.If(driver is null, $"requires a live Neo4j at {BoltUri}");

        var projectId = "blast-radius-empty-" + Guid.NewGuid();
        try
        {
            var apiRepoDir = CreateRepoFixture(("ChatController.cs",
                "namespace ApiRepo;\n\n" +
                "[Route(\"api/chat\")]\n" +
                "public partial class ChatController\n{\n" +
                "    [HttpPost(\"query\")]\n" +
                "    public void Query() { }\n" +
                "}\n"));

            await ImportAsync(projectId, apiRepoDir, "api-repo", driver!, CancellationToken.None);

            var tool = new BlastRadiusQueryTool(driver!, "neo4j");
            var result = await tool.QueryAsync(projectId, ["ChatController.cs"], CancellationToken.None);

            Assert.Empty(result); // no false positives -- nothing calls this endpoint
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

    [Fact]
    public async Task QueryAsync_ReturnsEmpty_WhenNoChangedFilesGiven()
    {
        // No live Neo4j needed -- the empty-input short-circuit happens before any session is opened.
        var tool = new BlastRadiusQueryTool(null!, "neo4j");

        var result = await tool.QueryAsync("proj1", [], CancellationToken.None);

        Assert.Empty(result);
    }
}
