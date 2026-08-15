namespace SupportForge.Core;

public interface IOrgMembershipRepository
{
    Task<bool> IsMemberAsync(string userId, string orgId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetOrgIdsForUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetUserIdsForOrgAsync(string orgId, CancellationToken ct = default);
    Task AddAsync(string userId, string orgId, CancellationToken ct = default);
    Task DeleteByOrgIdAsync(string orgId, CancellationToken ct = default);
}
