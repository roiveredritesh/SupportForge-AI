using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SupportForge.VectorStore.Models;

namespace SupportForge.VectorStore.Chroma;

public sealed class ChromaVectorStoreService : IVectorStoreService
{
    private readonly HttpClient _http;
    private readonly string _collectionsPath;

    public ChromaVectorStoreService(HttpClient http, IOptions<ChromaOptions> options)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.Value.BaseUrl);
        _collectionsPath = $"/api/v2/tenants/{options.Value.Tenant}/databases/{options.Value.Database}/collections";
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collection, ct);

        var payload = new
        {
            ids = documents.Select(d => d.Id).ToArray(),
            documents = documents.Select(d => d.Text).ToArray(),
            embeddings = documents.Select(d => d.Embedding).ToArray(),
            metadatas = documents.Select(d => d.Metadata).ToArray(),
        };

        var response = await _http.PostAsJsonAsync($"{_collectionsPath}/{collectionId}/upsert", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VectorQueryResult>> QueryAsync(
        string collection,
        float[] queryEmbedding,
        int topK,
        IReadOnlyDictionary<string, string>? metadataFilter = null,
        CancellationToken ct = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collection, ct);

        var payload = new
        {
            query_embeddings = new[] { queryEmbedding },
            n_results = topK,
            where = metadataFilter,
        };

        var response = await _http.PostAsJsonAsync($"{_collectionsPath}/{collectionId}/query", payload, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChromaQueryResponse>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty Chroma response");

        var results = new List<VectorQueryResult>();
        for (var i = 0; i < body.Ids[0].Count; i++)
        {
            results.Add(new VectorQueryResult(
                body.Ids[0][i],
                body.Documents[0][i],
                body.Distances[0][i],
                body.Metadatas[0][i]));
        }

        return results;
    }

    public async Task DeleteAsync(string collection, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collection, ct);
        var response = await _http.PostAsJsonAsync($"{_collectionsPath}/{collectionId}/delete", new { ids }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"{_collectionsPath}/{collection}", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    public async Task<long> CountAsync(string collection, CancellationToken ct = default)
    {
        var collectionId = await ResolveCollectionIdAsync(collection, ct);
        var response = await _http.GetAsync($"{_collectionsPath}/{collectionId}/count", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<long>(cancellationToken: ct);
    }

    // Chroma's v2 API resolves GET/DELETE collection routes by name, but upsert/query/delete-records
    // routes require the collection's UUID, so those need a get_or_create round-trip first.
    private async Task<string> ResolveCollectionIdAsync(string collection, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(_collectionsPath, new { name = collection, get_or_create = true }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChromaCollection>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty Chroma response");
        return body.Id;
    }

    private sealed class ChromaCollection
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ChromaQueryResponse
    {
        public List<List<string>> Ids { get; set; } = new();
        public List<List<string>> Documents { get; set; } = new();
        public List<List<float>> Distances { get; set; } = new();
        public List<List<Dictionary<string, string>>> Metadatas { get; set; } = new();
    }
}
