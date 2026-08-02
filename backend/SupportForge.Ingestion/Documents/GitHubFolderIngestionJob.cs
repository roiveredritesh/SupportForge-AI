using SupportForge.Core;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// A <see cref="KbSourceType.Documents"/> source whose Location is a raw github.com URL: clones (or
/// pulls) the repo standalone -- independent of <see cref="Core.Entities.Project.Repos"/>, since the
/// URL is self-contained -- then extracts the resolved subfolder same as a local <see cref="DocumentIngestionJob"/>.
/// </summary>
public sealed class GitHubFolderIngestionJob : IIngestionJob
{
    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localRepoPath;
    private readonly string _subPath;
    private readonly string _sourceLocation;
    private readonly GitRepoSyncService _gitSync;
    private readonly GraphifyCliRunner _graphify;
    private readonly IReadOnlyDictionary<string, string?> _graphifyEnvironment;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public GitHubFolderIngestionJob(
        string projectId, string repoUrl, string branch, string localRepoPath, string subPath, string sourceLocation,
        GitRepoSyncService gitSync, GraphifyCliRunner graphify, IReadOnlyDictionary<string, string?> graphifyEnvironment,
        IProjectRepository projects)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localRepoPath = localRepoPath;
        _subPath = subPath;
        _sourceLocation = sourceLocation;
        _gitSync = gitSync;
        _graphify = graphify;
        _graphifyEnvironment = graphifyEnvironment;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_localRepoPath);
        _gitSync.CloneOrPull(_repoUrl, _localRepoPath, _branch);

        var folderPath = string.IsNullOrEmpty(_subPath) ? _localRepoPath : Path.Combine(_localRepoPath, _subPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(
                $"KB source '{_sourceLocation}' resolved to '{folderPath}' after cloning, but that path doesn't exist in the repo.");

        await _graphify.RunAsync(folderPath, _graphifyEnvironment, ct, "extract", ".");

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
