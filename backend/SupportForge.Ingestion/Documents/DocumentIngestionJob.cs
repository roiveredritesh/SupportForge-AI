using Docnet.Core;
using Docnet.Core.Models;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJob : IIngestionJob
{
    private readonly string _folderPath;
    private readonly string _sourceLocation;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public DocumentIngestionJob(string projectId, string folderPath, string sourceLocation, ILlmClient llm, IVectorStoreService vectorStore, IProjectRepository projects)
    {
        ProjectId = projectId;
        _folderPath = folderPath;
        _sourceLocation = sourceLocation;
        _llm = llm;
        _vectorStore = vectorStore;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folderPath))
            throw new DirectoryNotFoundException($"KB folder '{_folderPath}' (source '{_sourceLocation}') not found for project '{ProjectId}'.");

        var files = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md") || f.EndsWith(".txt") || f.EndsWith(".pdf"));

        var documents = new List<VectorDocument>();
        foreach (var file in files)
        {
            var text = file.EndsWith(".pdf") ? ExtractPdfText(file) : await File.ReadAllTextAsync(file, ct);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chunks = DocumentChunker.Chunk(text);

            for (var i = 0; i < chunks.Count; i++)
            {
                var embedding = await _llm.EmbedAsync(chunks[i], ct);
                documents.Add(new VectorDocument(
                    Id: $"{Path.GetFileNameWithoutExtension(file)}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: new Dictionary<string, string> { ["source"] = file, ["chunk"] = i.ToString() }));
            }
        }

        static string ExtractPdfText(string file)
        {
            try
            {
                using var reader = DocLib.Instance.GetDocReader(file, new PageDimensions(1080, 1920));
                var text = new System.Text.StringBuilder();
                for (var i = 0; i < reader.GetPageCount(); i++)
                {
                    using var page = reader.GetPageReader(i);
                    text.AppendLine(page.GetText());
                }
                return text.ToString();
            }
            catch (Exception)
            {
                // ponytail: a single corrupt/unreadable PDF shouldn't abort the whole ingestion job;
                // treat it like an empty file so the loop's blank-content check skips it.
                return string.Empty;
            }
        }

        if (documents.Count == 0)
            throw new InvalidOperationException(
                $"KB folder '{_folderPath}' (source '{_sourceLocation}') contained no readable .md/.txt/.pdf content for project '{ProjectId}'.");

        await _vectorStore.UpsertAsync($"{ProjectId}-kb", documents, ct);

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
