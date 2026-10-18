using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Graph;

/// <summary>
/// Semantic code search: an embedding per top-level definition (class, struct, interface, top-level function) in a
/// per-project vector collection, so a question can find code by meaning when it shares no words with it ("which class
/// signs values so they cannot be tampered with?" -> Signer). <see cref="GraphDbQueryTool"/> merges these hits with its
/// full-text matches. What is embedded is derived from the customer's code (names, doc comments, body terms) and goes to
/// the configured embedding provider, so indexing runs only under the same org and project consent as the code
/// classifier (CodeClassificationEnabled on both); without it a repository's vectors are removed.
/// </summary>
public static class CodeVectorIndex
{
    public static string CollectionName(string projectId) => $"{projectId}-code";

    // Long enough for a name, a doc-comment summary and the most frequent body terms.
    private const int MaxTextLength = 1000;
    private const int BodyTermsInText = 40;

    // Top-level definitions only: a member's id goes through its container ("file::Class.method"), and methods would
    // multiply the embedding cost while their class already carries their body terms.
    public static bool IsIndexable(CodeGraphNode node) =>
        !node.IsTest
        && !node.Id.StartsWith("endpoint::", StringComparison.Ordinal)
        && node.Id.IndexOf("::", StringComparison.Ordinal) is var separator and > 0
        && node.Id.IndexOf('.', separator + 2) < 0;

    public static string Text(CodeGraphNode node)
    {
        var body = string.Join(' ', node.BodyTerms.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(BodyTermsInText));
        var text = $"{node.Label}: {node.Summary} {body}".Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    public static string DocumentId(string repo, string nodeId) => $"{repo}::{nodeId}";

    // Texts per embedding request.
    private const int BatchSize = 64;

    /// <summary>Embeds a repository's indexable definitions into the project's collection; returns how many.</summary>
    public static async Task<int> IndexAsync(
        IVectorStoreService store, ILlmEmbeddingClient embeddings, string projectId, string repo, CodeGraphFile graph, CancellationToken ct)
    {
        var collection = CollectionName(projectId);
        var nodes = graph.Nodes.Where(IsIndexable).ToList();
        for (var start = 0; start < nodes.Count; start += BatchSize)
        {
            var batch = nodes.Skip(start).Take(BatchSize).ToList();
            var texts = batch.Select(Text).ToList();
            var vectors = await embeddings.EmbedManyAsync(texts, ct, EmbeddingPurpose.Passage);
            await store.UpsertAsync(collection, batch.Select((n, i) => new VectorDocument(
                DocumentId(repo, n.Id),
                texts[i],
                vectors[i],
                new Dictionary<string, string> { ["repo"] = repo, ["nodeId"] = n.Id })).ToList(), ct);
        }
        return nodes.Count;
    }
}

/// <summary>Finds definitions by meaning in a project's <see cref="CodeVectorIndex"/> collection.</summary>
public interface ICodeSemanticSearch
{
    /// <summary>The nearest definitions as (repo, node id), closest first; empty when the project has no code vectors.</summary>
    Task<IReadOnlyList<(string Repo, string NodeId)>> SearchAsync(string projectId, string question, int topK, CancellationToken ct = default);
}

public sealed class VectorCodeSemanticSearch : ICodeSemanticSearch
{
    private readonly IVectorStoreService _store;
    private readonly ILlmEmbeddingClient _embeddings;

    public VectorCodeSemanticSearch(IVectorStoreService store, ILlmEmbeddingClient embeddings)
    {
        _store = store;
        _embeddings = embeddings;
    }

    public async Task<IReadOnlyList<(string Repo, string NodeId)>> SearchAsync(string projectId, string question, int topK, CancellationToken ct = default)
    {
        var collection = CodeVectorIndex.CollectionName(projectId);
        // A project that never consented has no vectors: no embedding call is made for its questions.
        if (await _store.CountAsync(collection, ct) == 0) return [];

        var embedding = await _embeddings.EmbedAsync(question, ct, EmbeddingPurpose.Query);
        var results = await _store.QueryAsync(collection, embedding, topK, ct: ct);
        return results
            .Where(r => r.Metadata.ContainsKey("repo") && r.Metadata.ContainsKey("nodeId"))
            .Select(r => (r.Metadata["repo"], r.Metadata["nodeId"]))
            .ToList();
    }
}

/// <summary>
/// Embeds a repository's top-level definitions into its project's code collection after <see cref="GraphImportJob"/>,
/// replacing that repository's previous vectors. Only with consent (see <see cref="CodeVectorIndex"/>); otherwise it
/// removes them.
/// </summary>
public sealed class CodeVectorIndexJob : IIngestionJob
{
    private readonly string _graphJsonPath;
    private readonly string _repo;
    private readonly IVectorStoreService _store;
    private readonly ILlmEmbeddingClient _embeddings;
    private readonly IProjectRepository _projects;
    private readonly IOrgRepository _orgs;
    private readonly ILogger _logger;

    public string ProjectId { get; }

    public CodeVectorIndexJob(
        string projectId, string graphJsonPath, string repo, IVectorStoreService store, ILlmEmbeddingClient embeddings,
        IProjectRepository projects, IOrgRepository orgs, ILogger logger)
    {
        ProjectId = projectId;
        _graphJsonPath = graphJsonPath;
        _repo = repo;
        _store = store;
        _embeddings = embeddings;
        _projects = projects;
        _orgs = orgs;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var collection = CodeVectorIndex.CollectionName(ProjectId);
        await _store.DeleteByMetadataAsync(collection, new Dictionary<string, string> { ["repo"] = _repo }, ct);

        if (!await HasConsentAsync(ct) || !File.Exists(_graphJsonPath)) return;

        var graph = JsonSerializer.Deserialize<CodeGraphFile>(await File.ReadAllTextAsync(_graphJsonPath, ct)) ?? new CodeGraphFile();
        var count = await CodeVectorIndex.IndexAsync(_store, _embeddings, ProjectId, _repo, graph, ct);

        // Counts only, never code text.
        _logger.LogInformation("CodeVectorIndex project={ProjectId} definitions={Count}", ProjectId, count);
    }

    private async Task<bool> HasConsentAsync(CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project is not { CodeClassificationEnabled: true, OrgId: not null }) return false;
        var org = await _orgs.GetByIdAsync(project.OrgId, ct);
        return org is { CodeClassificationEnabled: true };
    }
}

/// <summary>One <see cref="CodeVectorIndexJob"/> per repo; registered after <see cref="GraphImportJobFactory"/>.</summary>
public sealed class CodeVectorIndexJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;
    private readonly string _cacheRoot;

    public CodeVectorIndexJobFactory(IServiceProvider services, string cacheRoot)
    {
        _services = services;
        _cacheRoot = cacheRoot;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project, string? triggeredByUserId)
    {
        var store = _services.GetRequiredService<IVectorStoreService>();
        var embeddings = _services.GetRequiredService<ILlmEmbeddingClient>();
        var projects = _services.GetRequiredService<IProjectRepository>();
        var orgs = _services.GetRequiredService<IOrgRepository>();
        var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger<CodeVectorIndexJob>();

        return project.Repos.Select(r => new CodeVectorIndexJob(
            project.Id,
            Path.Combine(_cacheRoot, project.Id, r.Repo, "code-graph", "graph.json"),
            r.Repo,
            store, embeddings, projects, orgs, logger)).ToList();
    }
}
