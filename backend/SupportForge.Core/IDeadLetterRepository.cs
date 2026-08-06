using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IDeadLetterRepository
{
    Task AddAsync(DeadLetterEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<DeadLetterEntry>> GetByProjectIdAsync(string projectId, CancellationToken ct = default);
    Task<DeadLetterEntry?> GetByIdAsync(string id, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
