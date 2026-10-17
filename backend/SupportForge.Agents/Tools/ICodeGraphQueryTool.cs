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

/// <summary>Shared format markers in <see cref="ICodeGraphQueryTool.QueryAsync"/>'s output.</summary>
public static class CodeGraphOutput
{
    /// <summary>
    /// Starts every line of a matched definition's source excerpt. DrafterAgent.LooksLikeLeak reads these lines to tell
    /// actual code (never to be copied into a draft) apart from the NODE/EDGE metadata around it.
    /// </summary>
    public const string ExcerptLinePrefix = "    | ";
}
