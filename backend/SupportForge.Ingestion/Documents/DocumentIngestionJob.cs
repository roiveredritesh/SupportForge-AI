using SupportForge.Agents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJob : IIngestionJob
{
    private readonly string _folderPath;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public string ProjectId { get; }

    public DocumentIngestionJob(string projectId, string folderPath, ILlmClient llm, IVectorStoreService vectorStore)
    {
        ProjectId = projectId;
        _folderPath = folderPath;
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folderPath)) return;

        var files = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md") || f.EndsWith(".txt"));

        var documents = new List<VectorDocument>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, ct);
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

        if (documents.Count > 0)
            await _vectorStore.UpsertAsync($"{ProjectId}-kb", documents, ct);
    }
}
