using SupportForge.Agents;
using SupportForge.Core;
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
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly GitRepoSyncService _gitSync;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public CodeIngestionJob(string projectId, string repoUrl, string branch, string localCachePath,
        string repoOwner, string repoName, GitRepoSyncService gitSync, ILlmClient llm, IVectorStoreService vectorStore, IProjectRepository projects)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localCachePath = localCachePath;
        _repoOwner = repoOwner;
        _repoName = repoName;
        _gitSync = gitSync;
        _llm = llm;
        _vectorStore = vectorStore;
        _projects = projects;
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
            // ponytail: code tokenizes far denser than prose (observed ~0.29 tokens/char for C#), so the
            // 2000-char cap blew past the embedding model's 512-token limit and NVIDIA rejected the
            // request with 400. 1000 chars keeps code chunks under that limit with margin.
            var chunks = DocumentChunker.Chunk(text, maxChars: 1000);

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

        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project != null)
        {
            var repoIndex = project.Repos.FindIndex(r => r.Owner == _repoOwner && r.Repo == _repoName);
            if (repoIndex >= 0)
            {
                project.Repos[repoIndex] = project.Repos[repoIndex] with { LastSyncedAt = DateTimeOffset.UtcNow };
                await _projects.UpsertAsync(project, ct);
            }
        }
    }
}
