using Microsoft.Extensions.DependencyInjection;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;
    private readonly string _repoCacheRoot;

    public DocumentIngestionJobFactory(IServiceProvider services, string repoCacheRoot)
    {
        _services = services;
        _repoCacheRoot = repoCacheRoot;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project)
    {
        var llm = _services.GetRequiredService<ILlmClient>();
        var vectorStore = _services.GetRequiredService<IVectorStoreService>();
        var projects = _services.GetRequiredService<IProjectRepository>();

        // ponytail: docs live inside the project's already-cloned code repo rather than some
        // separately-fetched location, so resolve against the first linked repo's local clone
        // (same path CodeIngestionJobFactory clones into) instead of adding a GitHub API client.
        var firstRepo = project.Repos.FirstOrDefault();
        string ResolveFolderPath(string location) => firstRepo is null
            ? location
            : Path.Combine(_repoCacheRoot, project.Id, firstRepo.Repo, location);

        return project.KbSources
            .Where(s => s.Type == KbSourceType.Documents)
            .Select(s => new DocumentIngestionJob(project.Id, ResolveFolderPath(s.Location), s.Location, llm, vectorStore, projects))
            .ToList();
    }
}
