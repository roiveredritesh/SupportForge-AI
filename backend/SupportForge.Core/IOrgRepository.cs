using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IOrgRepository
{
    Task<IReadOnlyList<Org>> GetAllAsync(CancellationToken ct = default);
    Task<Org?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Org org, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
