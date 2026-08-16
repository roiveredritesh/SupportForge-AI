using SupportForge.Core;

namespace SupportForge.Ingestion.Documents;

public sealed class ConfluenceIngestionJob : IIngestionJob
{
    private readonly string _pageId;
    private readonly ConfluencePageFetcher _fetcher;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;
    private readonly string? _triggeredByUserId;

    public string ProjectId { get; }

    public ConfluenceIngestionJob(
        string projectId, string pageId, ConfluencePageFetcher fetcher, KbVectorIndexer indexer, IProjectRepository projects,
        string? triggeredByUserId = null)
    {
        ProjectId = projectId;
        _pageId = pageId;
        _fetcher = fetcher;
        _indexer = indexer;
        _projects = projects;
        _triggeredByUserId = triggeredByUserId;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var (title, markdown) = await _fetcher.FetchPageAsMarkdownAsync(_pageId, ct);

        await _indexer.IndexAsync(ProjectId, [(_pageId, markdown, title)], ct, _triggeredByUserId);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _pageId, ct);
    }
}
