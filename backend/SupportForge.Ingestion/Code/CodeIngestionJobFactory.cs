using Microsoft.Extensions.DependencyInjection;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

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
        var llm = _services.GetRequiredService<ILlmClient>();
        var vectorStore = _services.GetRequiredService<IVectorStoreService>();
        var projects = _services.GetRequiredService<IProjectRepository>();

        return project.Repos.Select(r => new CodeIngestionJob(
            project.Id,
            $"https://github.com/{r.Owner}/{r.Repo}.git",
            r.DefaultBranch,
            Path.Combine(_cacheRoot, project.Id, r.Repo),
            r.Owner,
            r.Repo,
            gitSync, llm, vectorStore, projects)).ToList();
    }
}
