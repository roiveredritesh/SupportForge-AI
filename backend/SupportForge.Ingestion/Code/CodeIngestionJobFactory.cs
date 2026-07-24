using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJobFactory : IIngestionJobFactory
{
    private readonly GitRepoSyncService _gitSync;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly string _cacheRoot;
    private readonly IProjectRepository _projects;

    public CodeIngestionJobFactory(GitRepoSyncService gitSync, ILlmClient llm, IVectorStoreService vectorStore, string cacheRoot, IProjectRepository projects)
    {
        _gitSync = gitSync;
        _llm = llm;
        _vectorStore = vectorStore;
        _cacheRoot = cacheRoot;
        _projects = projects;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project) =>
        project.Repos.Select(r => new CodeIngestionJob(
            project.Id,
            $"https://github.com/{r.Owner}/{r.Repo}.git",
            r.DefaultBranch,
            Path.Combine(_cacheRoot, project.Id, r.Repo),
            r.Owner,
            r.Repo,
            _gitSync, _llm, _vectorStore, _projects));
}
