using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default);
    Task<Project?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Project project, CancellationToken ct = default);
}
