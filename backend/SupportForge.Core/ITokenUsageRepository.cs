using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface ITokenUsageRepository
{
    Task AddAsync(TokenUsageEntry entry, CancellationToken ct = default);
    Task<int> GetTotalForProjectAsync(string projectId, CancellationToken ct = default);
}
