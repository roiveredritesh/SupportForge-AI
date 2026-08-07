using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface ITokenUsageRepository
{
    Task AddAsync(TokenUsageEntry entry, CancellationToken ct = default);
    Task<int> GetTotalForProjectAsync(string projectId, CancellationToken ct = default);
    Task<Dictionary<string, int>> GetTotalsBySourceForProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
