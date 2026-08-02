using Microsoft.Extensions.Logging.Abstractions;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class CodeGraphMergeJobTests
{
    private static async Task<string> ExtractFixtureGraphAsync(GraphifyCliRunner graphify, string fileName, string content)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(Path.Combine(repoDir, fileName), content);
        await graphify.RunAsync(repoDir, environment: null, CancellationToken.None, "extract", ".", "--no-cluster");
        return Path.Combine(repoDir, "graphify-out", "graph.json");
    }

    [Fact]
    public async Task RunAsync_TwoRepos_MergesGraphs_WithRepoTaggedNodes()
    {
        var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
        var graphA = await ExtractFixtureGraphAsync(graphify, "a.py", "class Auth:\n    def login(self): pass\n");
        var graphB = await ExtractFixtureGraphAsync(graphify, "b.py", "class Billing:\n    def charge(self): pass\n");
        var outputDir = Directory.CreateTempSubdirectory().FullName;
        var outputPath = Path.Combine(outputDir, "graph.json");

        var job = new CodeGraphMergeJob("proj1", [graphA, graphB], outputPath, graphify);
        await job.RunAsync(CancellationToken.None);

        Assert.True(File.Exists(outputPath));
        var merged = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("\"repo\"", merged);
    }

    [Fact]
    public async Task RunAsync_SingleRepo_CopiesItsOwnGraph_WithoutCallingMergeGraphs()
    {
        var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
        var graphA = await ExtractFixtureGraphAsync(graphify, "a.py", "class Auth:\n    def login(self): pass\n");
        var outputDir = Directory.CreateTempSubdirectory().FullName;
        var outputPath = Path.Combine(outputDir, "graph.json");

        var job = new CodeGraphMergeJob("proj1", [graphA], outputPath, graphify);
        await job.RunAsync(CancellationToken.None);

        Assert.True(File.Exists(outputPath));
        // merge-graphs requires 2+ inputs and would fail loudly if invoked with one -- a successful
        // run here proves the copy fallback ran, not merge-graphs.
        Assert.Equal(await File.ReadAllTextAsync(graphA), await File.ReadAllTextAsync(outputPath));
    }
}
