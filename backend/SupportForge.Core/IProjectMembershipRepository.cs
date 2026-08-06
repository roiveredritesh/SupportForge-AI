namespace SupportForge.Core;

public interface IProjectMembershipRepository
{
    Task<bool> IsMemberAsync(string userId, string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetProjectIdsForUserAsync(string userId, CancellationToken ct = default);
    Task AddAsync(string userId, string projectId, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
