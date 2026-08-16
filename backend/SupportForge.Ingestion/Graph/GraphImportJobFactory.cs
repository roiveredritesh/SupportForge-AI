using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using SupportForge.Core.Entities;

namespace SupportForge.Ingestion.Graph;

/// <summary>
/// One <see cref="GraphImportJob"/> per repo, run after that repo's <c>CodeIngestionJob</c> --
/// registration order after <c>CodeIngestionJobFactory</c> in <c>Program.cs</c> guarantees this via
/// the FIFO-queue ordering the ingestion queue drains in.
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

    // Signature-only per U7 -- this factory never writes a TokenUsageEntry (no KbVectorIndexer
    // call), so triggeredByUserId is unused here.
    public IEnumerable<IIngestionJob> CreateJobs(Project project, string? triggeredByUserId)
    {
        var driver = _services.GetRequiredService<IDriver>();
        var database = _services.GetRequiredService<Neo4jOptions>().Database;

        return project.Repos.Select(r => new GraphImportJob(
            project.Id,
            Path.Combine(_cacheRoot, project.Id, r.Repo, "code-graph", "graph.json"),
            r.Repo,
            driver,
            database)).ToList();
    }
}
