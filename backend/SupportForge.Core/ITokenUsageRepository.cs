using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface ITokenUsageRepository
{
    Task AddAsync(TokenUsageEntry entry, CancellationToken ct = default);
    Task<int> GetTotalForProjectAsync(string projectId, CancellationToken ct = default);
    Task<Dictionary<string, int>> GetTotalsBySourceForProjectAsync(string projectId, CancellationToken ct = default);
    // U4: raw entries for a project -- ProjectsController.GetQueryVolume day-buckets these itself
    // (Source=="chat" filter + CreatedAt grouping), no new aggregate method needed for that shape.
    Task<IReadOnlyList<TokenUsageEntry>> GetEntriesForProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
