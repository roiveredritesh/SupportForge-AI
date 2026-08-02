using SupportForge.Core;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJob : IIngestionJob
{
    private static readonly string[] SupportedExtensions = [".md", ".txt"];

    private readonly string _folderPath;
    private readonly string _sourceLocation;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public DocumentIngestionJob(
        string projectId, string folderPath, string sourceLocation, KbVectorIndexer indexer, IProjectRepository projects)
    {
        ProjectId = projectId;
        _folderPath = folderPath;
        _sourceLocation = sourceLocation;
        _indexer = indexer;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folderPath))
            throw new DirectoryNotFoundException($"KB folder '{_folderPath}' (source '{_sourceLocation}') not found for project '{ProjectId}'.");

        var files = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

        var documents = new List<(string SourceRef, string Text)>();
        foreach (var file in files)
            documents.Add((file, await File.ReadAllTextAsync(file, ct)));

        await _indexer.IndexAsync(ProjectId, documents, ct);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _sourceLocation, ct);
    }
}
