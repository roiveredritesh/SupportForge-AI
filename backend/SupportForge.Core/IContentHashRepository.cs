namespace SupportForge.Core;

// D1 (gap-closing-solutions.md Phase D, item 1): lets KbVectorIndexer skip re-chunk/re-embed for a
// document whose content hasn't changed since the last successful index, keyed by
// {projectId}:{sourceRef} since the same sourceRef (e.g. a file path) can repeat across projects.
public interface IContentHashRepository
{
    Task<string?> GetHashAsync(string projectId, string sourceRef, CancellationToken ct = default);
    Task SetHashAsync(string projectId, string sourceRef, string hash, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
