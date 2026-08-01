using SupportForge.Core;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Documents;

public sealed class ConfluenceIngestionJob : IIngestionJob
{
    private readonly string _pageId;
    private readonly string _stagingFolder;
    private readonly ConfluencePageFetcher _fetcher;
    private readonly GraphifyCliRunner _graphify;
    private readonly IReadOnlyDictionary<string, string?> _graphifyEnvironment;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public ConfluenceIngestionJob(
        string projectId, string pageId, string stagingFolder, ConfluencePageFetcher fetcher,
        GraphifyCliRunner graphify, IReadOnlyDictionary<string, string?> graphifyEnvironment, IProjectRepository projects)
    {
        ProjectId = projectId;
        _pageId = pageId;
        _stagingFolder = stagingFolder;
        _fetcher = fetcher;
        _graphify = graphify;
        _graphifyEnvironment = graphifyEnvironment;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_stagingFolder);
        var (_, markdown) = await _fetcher.FetchPageAsMarkdownAsync(_pageId, ct);
        await File.WriteAllTextAsync(Path.Combine(_stagingFolder, $"{_pageId}.md"), markdown, ct);

        await _graphify.RunAsync(_stagingFolder, _graphifyEnvironment, ct, "extract", ".");

        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project != null)
        {
            var sourceIndex = project.KbSources.FindIndex(s => s.Location == _pageId);
            if (sourceIndex >= 0)
            {
                project.KbSources[sourceIndex] = project.KbSources[sourceIndex] with { LastSyncedAt = DateTimeOffset.UtcNow };
                await _projects.UpsertAsync(project, ct);
            }
        }
    }
}
