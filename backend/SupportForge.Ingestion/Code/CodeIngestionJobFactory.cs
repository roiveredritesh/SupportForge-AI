using Microsoft.Extensions.DependencyInjection;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;
    private readonly string _cacheRoot;

    public CodeIngestionJobFactory(IServiceProvider services, string cacheRoot)
    {
        _services = services;
        _cacheRoot = cacheRoot;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project)
    {
        var gitSync = _services.GetRequiredService<GitRepoSyncService>();
        var graphify = _services.GetRequiredService<GraphifyCliRunner>();
        var projects = _services.GetRequiredService<IProjectRepository>();

        return project.Repos.Select(r => new CodeIngestionJob(
            project.Id,
            $"https://github.com/{r.Owner}/{r.Repo}.git",
            r.DefaultBranch,
            Path.Combine(_cacheRoot, project.Id, r.Repo),
            r.Owner,
            r.Repo,
            gitSync, graphify, projects)).ToList();
    }
}
