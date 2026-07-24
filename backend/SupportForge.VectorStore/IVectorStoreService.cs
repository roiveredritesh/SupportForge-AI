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

    Task DeleteCollectionAsync(string collection, CancellationToken ct = default);
}
