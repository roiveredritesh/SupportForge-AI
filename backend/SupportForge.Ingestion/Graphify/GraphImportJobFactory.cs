using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using SupportForge.Core.Entities;

namespace SupportForge.Ingestion.Graphify;

/// <summary>
/// WS1 (retrieval-pipeline remediation plan): one <see cref="GraphImportJob"/> per repo, run after that
/// repo's <c>CodeIngestionJob</c> extract step -- registration order after
/// <c>CodeIngestionJobFactory</c> in <c>Program.cs</c> guarantees this via the same FIFO-queue ordering
/// <c>CodeGraphMergeJobFactory</c> already relies on.
/// </summary>
public sealed class GraphImportJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;
    private readonly string _cacheRoot;

    public GraphImportJobFactory(IServiceProvider services, string cacheRoot)
    {
        _services = services;
        _cacheRoot = cacheRoot;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project)
    {
        var driver = _services.GetRequiredService<IDriver>();
        var database = _services.GetRequiredService<Neo4jOptions>().Database;

        return project.Repos.Select(r => new GraphImportJob(
            project.Id,
            Path.Combine(_cacheRoot, project.Id, r.Repo, "graphify-out", "graph.json"),
            r.Repo,
            driver,
            database)).ToList();
    }
}
