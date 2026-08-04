namespace SupportForge.Agents.Tools;

/// <summary>
/// Queries a project's code knowledge graph (Neo4j, tagged by projectId). Interface lives here (not
/// alongside its implementation) so <see cref="CodeAnalyzerAgent"/>'s tests can mock it directly,
/// matching how <see cref="ILlmEmbeddingClient"/>/<see cref="SupportForge.VectorStore.IVectorStoreService"/>
/// are interfaced for the same reason -- the concrete implementation lives in SupportForge.Ingestion,
/// which this project doesn't reference.
/// </summary>
public interface ICodeGraphQueryTool
{
    Task<string?> QueryAsync(string projectId, string question, bool retrying, CancellationToken ct = default);
}
