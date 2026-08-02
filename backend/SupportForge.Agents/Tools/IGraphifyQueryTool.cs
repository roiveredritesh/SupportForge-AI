namespace SupportForge.Agents.Tools;

/// <summary>
/// Queries a project's merged code knowledge graph via the graphify CLI. Interface lives here (not
/// alongside its implementation) so <see cref="CodeAnalyzerAgent"/>'s tests can mock it directly,
/// matching how <see cref="ILlmEmbeddingClient"/>/<see cref="SupportForge.VectorStore.IVectorStoreService"/>
/// are interfaced for the same reason -- the concrete implementation shells out to a real
/// subprocess and lives in SupportForge.Ingestion, which this project doesn't reference.
/// </summary>
public interface IGraphifyQueryTool
{
    Task<string?> QueryAsync(string projectId, string question, bool retrying, CancellationToken ct = default);
}
