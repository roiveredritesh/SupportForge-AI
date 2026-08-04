using SupportForge.Ingestion.Graph;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GraphImportJobTests
{
    [Fact]
    public async Task RunAsync_NoOpsGracefully_WhenGraphJsonDoesNotExistYet()
    {
        // No live Neo4j/Memgraph instance in this test run -- driver is never touched because the
        // early-return happens before any session is opened, so `null!` is safe here.
        var job = new GraphImportJob("proj1", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "graph.json"),
            "repo1", null!, "neo4j");

        await job.RunAsync(CancellationToken.None); // must not throw
    }
}
