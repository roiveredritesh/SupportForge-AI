namespace SupportForge.Agents.Tools;

// U24: one changed repo's endpoint, grouped by every other repo (in the same project) whose
// GraphDbQueryTool-Neo4j-backed calls_endpoint edge references it -- "Repo A -> used by Repo B, Repo C".
public sealed record BlastRadiusEntry(string Repo, IReadOnlyList<string> UsedBy);

/// <summary>
/// Traverses the project's code graph's <c>calls_endpoint</c> edges (added by
/// <see cref="SupportForge.Ingestion.Code.CodeGraphExtractor"/>, U23) to find, for a set of changed
/// files, every other repo in the same project that calls an endpoint one of those files defines.
/// Interface lives here (not alongside its implementation), same reasoning as
/// <see cref="ICodeGraphQueryTool"/>: the concrete implementation lives in SupportForge.Ingestion,
/// which this project doesn't reference, and <see cref="ChatController"/>'s tests need to mock it.
/// </summary>
public interface IBlastRadiusQueryTool
{
    Task<IReadOnlyList<BlastRadiusEntry>> QueryAsync(string projectId, IReadOnlyList<string> changedFiles, CancellationToken ct = default);
}
