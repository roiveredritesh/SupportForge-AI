using SupportForge.Core;

namespace SupportForge.Ingestion.Documents;

public sealed class ConfluenceIngestionJob : IIngestionJob
{
    private readonly string _pageId;
    private readonly ConfluencePageFetcher _fetcher;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public ConfluenceIngestionJob(
        string projectId, string pageId, ConfluencePageFetcher fetcher, KbVectorIndexer indexer, IProjectRepository projects)
    {
        ProjectId = projectId;
        _pageId = pageId;
        _fetcher = fetcher;
        _indexer = indexer;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var (title, markdown) = await _fetcher.FetchPageAsMarkdownAsync(_pageId, ct);

        await _indexer.IndexAsync(ProjectId, [(_pageId, markdown, title)], ct);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _pageId, ct);
    }
}
