using SupportForge.Core;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJob : IIngestionJob
{
    private readonly string _folderPath;
    private readonly string _sourceLocation;
    private readonly GraphifyCliRunner _graphify;
    private readonly IReadOnlyDictionary<string, string?> _graphifyEnvironment;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public DocumentIngestionJob(
        string projectId, string folderPath, string sourceLocation, GraphifyCliRunner graphify,
        IReadOnlyDictionary<string, string?> graphifyEnvironment, IProjectRepository projects)
    {
        ProjectId = projectId;
        _folderPath = folderPath;
        _sourceLocation = sourceLocation;
        _graphify = graphify;
        _graphifyEnvironment = graphifyEnvironment;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folderPath))
            throw new DirectoryNotFoundException($"KB folder '{_folderPath}' (source '{_sourceLocation}') not found for project '{ProjectId}'.");

        // graphify's own extraction handles docs/MD/TXT/PDF detection and parsing directly; this
        // path (unlike code) consumes LLM tokens for semantic extraction, so no --no-cluster here,
        // and it needs the derived backend environment (KTD3) code extraction doesn't.
        await _graphify.RunAsync(_folderPath, _graphifyEnvironment, ct, "extract", ".");

        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project != null)
        {
            var sourceIndex = project.KbSources.FindIndex(s => s.Location == _sourceLocation);
            if (sourceIndex >= 0)
            {
                project.KbSources[sourceIndex] = project.KbSources[sourceIndex] with { LastSyncedAt = DateTimeOffset.UtcNow };
                await _projects.UpsertAsync(project, ct);
            }
        }
    }
}
