using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IUserRepository
{
    Task<IReadOnlyList<AppUser>> GetAllAsync(CancellationToken ct = default);
    Task<AppUser?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(AppUser user, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
