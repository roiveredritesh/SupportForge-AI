using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SupportForge.VectorStore.Models;

namespace SupportForge.VectorStore.Pinecone;

/// <summary>
/// Second IVectorStoreService implementation (gap-closing-solutions.md Phase C, item 1) -- selected via
/// the same "VectorStore:Provider" config switch as Chroma. Every project's "{projectId}-kb" collection
/// maps directly to a Pinecone namespace within one shared index, matching Pinecone's own recommended
/// multi-tenancy pattern (one index, one namespace per tenant) rather than one index per project.
/// </summary>
public sealed class PineconeVectorStoreService : IVectorStoreService
{
    // Pinecone has no separate "document text" field (only vector values + metadata), unlike Chroma's
    // documents array -- stash VectorDocument.Text under a reserved metadata key at upsert time and
    // pull it back out at query time, transparent to callers.
    private const string TextMetadataKey = "_text";

    private readonly HttpClient _http;

    public PineconeVectorStoreService(HttpClient http, IOptions<PineconeOptions> options)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.Value.Host);
        if (!_http.DefaultRequestHeaders.Contains("Api-Key"))
            _http.DefaultRequestHeaders.Add("Api-Key", options.Value.ApiKey);
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct = default)
    {
        var vectors = documents.Select(d => new
        {
            id = d.Id,
            values = d.Embedding,
            metadata = WithText(d.Metadata, d.Text),
        });

        var response = await _http.PostAsJsonAsync("/vectors/upsert", new { vectors, @namespace = collection }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VectorQueryResult>> QueryAsync(
        string collection,
        float[] queryEmbedding,
        int topK,
        IReadOnlyDictionary<string, string>? metadataFilter = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            vector = queryEmbedding,
            topK,
            @namespace = collection,
            filter = ToPineconeFilter(metadataFilter),
            includeMetadata = true,
        };

        var response = await _http.PostAsJsonAsync("/query", payload, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PineconeQueryResponse>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty Pinecone response");

        return body.Matches.Select(m =>
        {
            var metadata = m.Metadata ?? new Dictionary<string, string>();
            metadata.Remove(TextMetadataKey, out var text);
            return new VectorQueryResult(m.Id, text ?? "", m.Score, metadata);
        }).ToList();
    }

    public async Task DeleteAsync(string collection, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/vectors/delete", new { ids, @namespace = collection }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        // deleteAll=true wipes every vector in the namespace but leaves the (implicit, schema-less)
        // namespace itself, which is fine -- Pinecone namespaces have no separate create/drop step.
        var response = await _http.PostAsJsonAsync("/vectors/delete", new { deleteAll = true, @namespace = collection }, ct);
        // A namespace that was never written to 404s on delete -- same "nothing to delete" outcome as
        // Chroma's DeleteCollectionAsync tolerating a 404 for a collection that never existed.
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    public async Task<long> CountAsync(string collection, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/describe_index_stats", new { }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PineconeStatsResponse>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty Pinecone response");

        return body.Namespaces.TryGetValue(collection, out var ns) ? ns.VectorCount : 0;
    }

    private static Dictionary<string, string> WithText(IReadOnlyDictionary<string, string> metadata, string text)
    {
        var withText = new Dictionary<string, string>(metadata) { [TextMetadataKey] = text };
        return withText;
    }

    private static object? ToPineconeFilter(IReadOnlyDictionary<string, string>? metadataFilter)
    {
        if (metadataFilter is null || metadataFilter.Count == 0) return null;
        // Pinecone's filter DSL wants each field as an explicit $eq operator, not a bare value.
        return metadataFilter.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, string> { ["$eq"] = kv.Value });
    }

    private sealed class PineconeQueryResponse
    {
        public List<PineconeMatch> Matches { get; set; } = new();
    }

    private sealed class PineconeMatch
    {
        public string Id { get; set; } = "";
        public float Score { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    private sealed class PineconeStatsResponse
    {
        public Dictionary<string, PineconeNamespaceStats> Namespaces { get; set; } = new();
    }

    private sealed class PineconeNamespaceStats
    {
        public long VectorCount { get; set; }
    }
}
