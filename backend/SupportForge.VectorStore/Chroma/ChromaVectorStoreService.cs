using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SupportForge.VectorStore.Models;

namespace SupportForge.VectorStore.Chroma;

public sealed class ChromaVectorStoreService : IVectorStoreService
{
    private readonly HttpClient _http;

    public ChromaVectorStoreService(HttpClient http, IOptions<ChromaOptions> options)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.Value.BaseUrl);
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct = default)
    {
        await EnsureCollectionAsync(collection, ct);

        var payload = new
        {
            ids = documents.Select(d => d.Id).ToArray(),
            documents = documents.Select(d => d.Text).ToArray(),
            embeddings = documents.Select(d => d.Embedding).ToArray(),
            metadatas = documents.Select(d => d.Metadata).ToArray(),
        };

        var response = await _http.PostAsJsonAsync($"/api/v1/collections/{collection}/upsert", payload, ct);
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
            query_embeddings = new[] { queryEmbedding },
            n_results = topK,
            where = metadataFilter,
        };

        var response = await _http.PostAsJsonAsync($"/api/v1/collections/{collection}/query", payload, ct);
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
        var response = await _http.PostAsJsonAsync($"/api/v1/collections/{collection}/delete", new { ids }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/collections/{collection}", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private async Task EnsureCollectionAsync(string collection, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/collections", new { name = collection, get_or_create = true }, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed class ChromaQueryResponse
    {
        public List<List<string>> Ids { get; set; } = new();
        public List<List<string>> Documents { get; set; } = new();
        public List<List<float>> Distances { get; set; } = new();
        public List<List<Dictionary<string, string>>> Metadatas { get; set; } = new();
    }
}
