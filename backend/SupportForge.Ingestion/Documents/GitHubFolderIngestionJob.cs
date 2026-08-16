using Microsoft.Extensions.Logging;
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
    private static readonly string[] SupportedExtensions = [".md", ".txt", ".pdf", ".docx", ".pptx"];

    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localRepoPath;
    private readonly string _subPath;
    private readonly string _sourceLocation;
    private readonly GitRepoSyncService _gitSync;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;
    private readonly ILogger<GitHubFolderIngestionJob> _logger;
    private readonly string? _triggeredByUserId;

    public string ProjectId { get; }

    public GitHubFolderIngestionJob(
        string projectId, string repoUrl, string branch, string localRepoPath, string subPath, string sourceLocation,
        GitRepoSyncService gitSync, KbVectorIndexer indexer, IProjectRepository projects,
        ILogger<GitHubFolderIngestionJob> logger, string? triggeredByUserId = null)
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
        _logger = logger;
        _triggeredByUserId = triggeredByUserId;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_localRepoPath);

        // U15: fetched ahead of the clone so a connected GitHub MCP connection on the project's org
        // can take precedence over the global GitHub:Token config.
        var project = await _projects.GetByIdAsync(ProjectId, ct);
        await _gitSync.CloneOrPullAsync(_repoUrl, _localRepoPath, _branch, project?.OrgId, ct);

        var folderPath = string.IsNullOrEmpty(_subPath) ? _localRepoPath : Path.Combine(_localRepoPath, _subPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException(
                $"KB source '{_sourceLocation}' resolved to '{folderPath}' after cloning, but that path doesn't exist in the repo.");

        var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var documents = new List<(string SourceRef, string Text, string? Title)>();
        foreach (var file in files)
        {
            string text;
            try
            {
                text = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".md" or ".txt" => await File.ReadAllTextAsync(file, ct),
                    _ => await DocumentTextExtractor.ExtractAsync(file, ct),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // U10: a file that fails to parse (corrupt/unsupported content) is logged and skipped
                // for that one file, not fatal to the whole folder walk.
                _logger.LogWarning(ex, "Failed to extract text from '{File}'; skipping file", file);
                continue;
            }
            documents.Add((file, text, null));
        }

        await _indexer.IndexAsync(
            ProjectId, documents, ct, _triggeredByUserId, new PruneScope(folderPath, files.ToHashSet()));
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _sourceLocation, ct);
    }
}
