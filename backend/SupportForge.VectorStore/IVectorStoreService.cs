using SupportForge.VectorStore.Models;

namespace SupportForge.VectorStore;

public interface IVectorStoreService
{
    Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct = default);

    Task<IReadOnlyList<VectorQueryResult>> QueryAsync(
        string collection,
        float[] queryEmbedding,
        int topK,
        IReadOnlyDictionary<string, string>? metadataFilter = null,
        CancellationToken ct = default);

    Task DeleteAsync(string collection, IReadOnlyList<string> ids, CancellationToken ct = default);

    // Deletes every chunk matching the given metadata (e.g. {"source": sourceRef}) without the
    // caller needing to know their exact chunk ids -- used to prune a removed source's stale chunks.
    Task DeleteByMetadataAsync(string collection, IReadOnlyDictionary<string, string> metadataFilter, CancellationToken ct = default);

    Task DeleteCollectionAsync(string collection, CancellationToken ct = default);

    Task<long> CountAsync(string collection, CancellationToken ct = default);
}
