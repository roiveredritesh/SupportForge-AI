using Microsoft.Extensions.DependencyInjection;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Code;

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
        var confluence = _services.GetRequiredService<ConfluencePageFetcher>();
        var projects = _services.GetRequiredService<IProjectRepository>();
        var gitSync = _services.GetRequiredService<GitRepoSyncService>();
        var indexer = _services.GetRequiredService<KbVectorIndexer>();
        var httpClientFactory = _services.GetRequiredService<IHttpClientFactory>();

        // A Documents source declares which repo it belongs to via RepoOwner/RepoName -- no more
        // silently resolving against project.Repos.FirstOrDefault(), which was wrong the moment a
        // project had peer repos. Null RepoOwner/RepoName means Location is a standalone path.
        string ResolveDocumentFolderPath(KbSourceConfig source)
        {
            if (source.RepoOwner is null && source.RepoName is null)
                return source.Location;

            var repo = project.Repos.FirstOrDefault(r => r.Owner == source.RepoOwner && r.Repo == source.RepoName);
            if (repo is null)
                throw new InvalidOperationException(
                    $"KB source '{source.Location}' on project '{project.Id}' references repo '{source.RepoOwner}/{source.RepoName}', which is not configured on this project.");

            return Path.Combine(_repoCacheRoot, project.Id, repo.Repo, source.Location);
        }

        IIngestionJob BuildDocumentsJob(KbSourceConfig s)
        {
            // A Location that's itself a github.com URL is self-contained -- clone it standalone
            // rather than requiring the repo to already be configured under project.Repos.
            if (s.RepoOwner is null && s.RepoName is null && GitHubFolderUrl.TryParse(s.Location, out var ghUrl))
            {
                var localRepoPath = Path.Combine(_repoCacheRoot, project.Id, "kb-github", ghUrl!.Owner, ghUrl.Repo);
                return new GitHubFolderIngestionJob(
                    project.Id, $"https://github.com/{ghUrl.Owner}/{ghUrl.Repo}.git", ghUrl.Branch,
                    localRepoPath, ghUrl.SubPath, s.Location, gitSync, indexer, projects);
            }

            return new DocumentIngestionJob(project.Id, ResolveDocumentFolderPath(s), s.Location, indexer, projects);
        }

        return project.KbSources.Select(s => (IIngestionJob)(s.Type switch
        {
            KbSourceType.Documents => BuildDocumentsJob(s),
            KbSourceType.Website => new WebsiteIngestionJob(project.Id, s.Location, httpClientFactory.CreateClient(), indexer, projects, s.CrawlLinkedPages),
            KbSourceType.Confluence => new ConfluenceIngestionJob(project.Id, s.Location, confluence, indexer, projects),
            _ => throw new NotSupportedException($"KB source type '{s.Type}' is not supported."),
        })).ToList();
    }
}
