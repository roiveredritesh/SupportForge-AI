using SupportForge.Core;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Folds a website/URL KB source into the project's graph via `graphify add &lt;url&gt;`, which
/// fetches the page into `./raw` and updates the graph in place -- no bespoke crawler needed.
/// </summary>
public sealed class WebsiteIngestionJob : IIngestionJob
{
    private readonly string _corpusPath;
    private readonly string _url;
    private readonly GraphifyCliRunner _graphify;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public WebsiteIngestionJob(string projectId, string corpusPath, string url, GraphifyCliRunner graphify, IProjectRepository projects)
    {
        ProjectId = projectId;
        _corpusPath = corpusPath;
        _url = url;
        _graphify = graphify;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_corpusPath);
        await _graphify.RunAsync(_corpusPath, environment: null, ct, "add", _url);

        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project != null)
        {
            var sourceIndex = project.KbSources.FindIndex(s => s.Location == _url);
            if (sourceIndex >= 0)
            {
                project.KbSources[sourceIndex] = project.KbSources[sourceIndex] with { LastSyncedAt = DateTimeOffset.UtcNow };
                await _projects.UpsertAsync(project, ct);
            }
        }
    }
}
