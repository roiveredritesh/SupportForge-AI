namespace SupportForge.Core;

// D1 (gap-closing-solutions.md Phase D, item 1): lets KbVectorIndexer skip re-chunk/re-embed for a
// document whose content hasn't changed since the last successful index, keyed by
// {projectId}:{sourceRef} since the same sourceRef (e.g. a file path) can repeat across projects.
public interface IContentHashRepository
{
    Task<string?> GetHashAsync(string projectId, string sourceRef, CancellationToken ct = default);
    Task SetHashAsync(string projectId, string sourceRef, string hash, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);

    // Lets KbVectorIndexer find sourceRefs that were hashed on a previous run but are no longer
    // present on this one (e.g. a file deleted from the repo since the last sync), so their stale
    // hash entry and vector-store chunks can be pruned instead of lingering forever.
    Task<IReadOnlyList<string>> GetSourceRefsAsync(string projectId, CancellationToken ct = default);
    Task DeleteAsync(string projectId, string sourceRef, CancellationToken ct = default);
}
