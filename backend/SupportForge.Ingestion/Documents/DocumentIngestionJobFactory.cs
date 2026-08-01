using Microsoft.Extensions.DependencyInjection;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Graphify;

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
        var graphify = _services.GetRequiredService<GraphifyCliRunner>();
        var confluence = _services.GetRequiredService<ConfluencePageFetcher>();
        var projects = _services.GetRequiredService<IProjectRepository>();

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

        return project.KbSources.Select(s => (IIngestionJob)(s.Type switch
        {
            KbSourceType.Documents => new DocumentIngestionJob(project.Id, ResolveDocumentFolderPath(s), s.Location, graphify, projects),
            KbSourceType.Website => new WebsiteIngestionJob(project.Id, Path.Combine(_repoCacheRoot, project.Id, "kb-web"), s.Location, graphify, projects),
            KbSourceType.Confluence => new ConfluenceIngestionJob(project.Id, s.Location, Path.Combine(_repoCacheRoot, project.Id, "kb-confluence"), confluence, graphify, projects),
            _ => throw new NotSupportedException($"KB source type '{s.Type}' is not supported."),
        })).ToList();
    }
}
