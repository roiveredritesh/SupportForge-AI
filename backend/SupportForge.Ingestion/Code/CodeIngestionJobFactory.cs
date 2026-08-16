using Microsoft.Extensions.DependencyInjection;
using SupportForge.Core;
using SupportForge.Core.Entities;

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

    // Signature-only per U7 -- this factory never writes a TokenUsageEntry (no KbVectorIndexer
    // call), so triggeredByUserId is unused here.
    public IEnumerable<IIngestionJob> CreateJobs(Project project, string? triggeredByUserId)
    {
        var gitSync = _services.GetRequiredService<GitRepoSyncService>();
        var projects = _services.GetRequiredService<IProjectRepository>();

        return project.Repos.Select(r => new CodeIngestionJob(
            project.Id,
            $"https://github.com/{r.Owner}/{r.Repo}.git",
            r.DefaultBranch,
            Path.Combine(_cacheRoot, project.Id, r.Repo),
            r.Owner,
            r.Repo,
            gitSync, projects)).ToList();
    }
}
