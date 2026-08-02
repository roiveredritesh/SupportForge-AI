using Microsoft.Extensions.DependencyInjection;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Code;

public sealed class CodeGraphMergeJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;
    private readonly string _cacheRoot;

    public CodeGraphMergeJobFactory(IServiceProvider services, string cacheRoot)
    {
        _services = services;
        _cacheRoot = cacheRoot;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project)
    {
        if (project.Repos.Count == 0) return [];

        var graphify = _services.GetRequiredService<GraphifyCliRunner>();

        // Always re-scan the project's CURRENT repo list fresh at run time (not a list captured
        // when some earlier trigger enqueued this job) -- correct under any trigger pattern,
        // including a future single-repo resync that doesn't re-trigger the whole project.
        var repoGraphPaths = project.Repos
            .Select(r => Path.Combine(_cacheRoot, project.Id, r.Repo, "graphify-out", "graph.json"))
            .ToList();
        var projectGraphPath = Path.Combine(_cacheRoot, project.Id, "graphify-project", "graph.json");

        return [new CodeGraphMergeJob(project.Id, repoGraphPaths, projectGraphPath, graphify)];
    }
}
