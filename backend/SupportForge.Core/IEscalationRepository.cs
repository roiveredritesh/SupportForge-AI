using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IEscalationRepository
{
    Task<IReadOnlyList<Escalation>> GetAllAsync(CancellationToken ct = default);
    Task<Escalation?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Escalation escalation, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
