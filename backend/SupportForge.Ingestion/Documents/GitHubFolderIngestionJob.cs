using SupportForge.Core;
using SupportForge.Ingestion.Code;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// A <see cref="KbSourceType.Documents"/> source whose Location is a raw github.com URL: clones (or
/// pulls) the repo standalone -- independent of <see cref="Core.Entities.Project.Repos"/>, since the
/// URL is self-contained -- then indexes the resolved subfolder same as a local <see cref="DocumentIngestionJob"/>.
/// </summary>
public sealed class GitHubFolderIngestionJob : IIngestionJob
{
    private static readonly string[] SupportedExtensions = [".md", ".txt"];

    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localRepoPath;
    private readonly string _subPath;
    private readonly string _sourceLocation;
    private readonly GitRepoSyncService _gitSync;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public GitHubFolderIngestionJob(
        string projectId, string repoUrl, string branch, string localRepoPath, string subPath, string sourceLocation,
        GitRepoSyncService gitSync, KbVectorIndexer indexer, IProjectRepository projects)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localRepoPath = localRepoPath;
        _subPath = subPath;
        _sourceLocation = sourceLocation;
        _gitSync = gitSync;
        _indexer = indexer;
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

        var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

        var documents = new List<(string SourceRef, string Text, string? Title)>();
        foreach (var file in files)
            documents.Add((file, await File.ReadAllTextAsync(file, ct), null));

        await _indexer.IndexAsync(ProjectId, documents, ct);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _sourceLocation, ct);
    }
}
