using Microsoft.Extensions.Logging.Abstractions;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GraphifyQueryToolTests
{
    private static async Task<string> BuildProjectGraphAsync(string cacheRoot, string projectId, GraphifyCliRunner graphify)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(Path.Combine(repoDir, "auth.py"),
            "class AuthService:\n    \"\"\"Handles user authentication.\"\"\"\n    def login(self): pass\n");
        await graphify.RunAsync(repoDir, environment: null, CancellationToken.None, "extract", ".", "--no-cluster");

        var projectGraphDir = Path.Combine(cacheRoot, projectId, "graphify-project");
        Directory.CreateDirectory(projectGraphDir);
        File.Copy(Path.Combine(repoDir, "graphify-out", "graph.json"), Path.Combine(projectGraphDir, "graph.json"));
        return projectGraphDir;
    }

    [Fact]
    public async Task QueryAsync_ReturnsNull_WhenProjectHasNoGraphYet()
    {
        var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
        var cacheRoot = Directory.CreateTempSubdirectory().FullName;
        var tool = new GraphifyQueryTool(graphify, cacheRoot);

        var result = await tool.QueryAsync("proj-never-ingested", "how does auth work", retrying: false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_ReturnsText_WhenGraphHasMatchingNodes()
    {
        var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
        var cacheRoot = Directory.CreateTempSubdirectory().FullName;
        await BuildProjectGraphAsync(cacheRoot, "proj1", graphify);
        var tool = new GraphifyQueryTool(graphify, cacheRoot);

        var result = await tool.QueryAsync("proj1", "authentication", retrying: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("AuthService", result);
    }

    [Fact]
    public async Task QueryAsync_ReturnsNull_WhenNoNodesMatch()
    {
        var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
        var cacheRoot = Directory.CreateTempSubdirectory().FullName;
        await BuildProjectGraphAsync(cacheRoot, "proj1", graphify);
        var tool = new GraphifyQueryTool(graphify, cacheRoot);

        var result = await tool.QueryAsync("proj1", "xyzzyplugh nonexistentterm", retrying: false, CancellationToken.None);

        Assert.Null(result);
    }
}
