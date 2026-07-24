using SupportForge.Agents;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJob : IIngestionJob
{
    private static readonly string[] CodeExtensions = { ".cs", ".ts", ".tsx", ".py", ".java", ".go" };

    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localCachePath;
    private readonly GitRepoSyncService _gitSync;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public string ProjectId { get; }

    public CodeIngestionJob(string projectId, string repoUrl, string branch, string localCachePath,
        GitRepoSyncService gitSync, ILlmClient llm, IVectorStoreService vectorStore)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localCachePath = localCachePath;
        _gitSync = gitSync;
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _gitSync.CloneOrPull(_repoUrl, _localCachePath, _branch);

        var files = Directory.EnumerateFiles(_localCachePath, "*.*", SearchOption.AllDirectories)
            .Where(f => CodeExtensions.Contains(Path.GetExtension(f)) && !f.Contains(".git"));

        var documents = new List<VectorDocument>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var relativePath = Path.GetRelativePath(_localCachePath, file);
            var chunks = DocumentChunker.Chunk(text, maxChars: 2000);

            for (var i = 0; i < chunks.Count; i++)
            {
                var embedding = await _llm.EmbedAsync(chunks[i], ct);
                documents.Add(new VectorDocument(
                    Id: $"{relativePath.Replace(Path.DirectorySeparatorChar, '_')}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: new Dictionary<string, string> { ["file"] = relativePath, ["chunk"] = i.ToString() }));
            }
        }

        if (documents.Count > 0)
            await _vectorStore.UpsertAsync($"{ProjectId}-code", documents, ct);
    }
}
