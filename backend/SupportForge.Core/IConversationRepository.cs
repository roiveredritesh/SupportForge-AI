using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IConversationRepository
{
    Task<IReadOnlyList<Conversation>> GetByProjectIdAsync(string projectId, CancellationToken ct = default);
    Task<Conversation?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Conversation conversation, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
