using Microsoft.Extensions.Logging;
using SupportForge.Core;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJob : IIngestionJob
{
    private static readonly string[] SupportedExtensions = [".md", ".txt", ".pdf", ".docx", ".pptx"];

    private readonly string _folderPath;
    private readonly string _sourceLocation;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;
    private readonly ILogger<DocumentIngestionJob> _logger;

    public string ProjectId { get; }

    public DocumentIngestionJob(
        string projectId, string folderPath, string sourceLocation, KbVectorIndexer indexer, IProjectRepository projects,
        ILogger<DocumentIngestionJob> logger)
    {
        ProjectId = projectId;
        _folderPath = folderPath;
        _sourceLocation = sourceLocation;
        _indexer = indexer;
        _projects = projects;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folderPath))
            throw new DirectoryNotFoundException($"KB folder '{_folderPath}' (source '{_sourceLocation}') not found for project '{ProjectId}'.");

        var files = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

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

        await _indexer.IndexAsync(ProjectId, documents, ct);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _sourceLocation, ct);
    }
}
