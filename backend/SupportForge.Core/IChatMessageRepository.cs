using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IChatMessageRepository
{
    Task<IReadOnlyList<ChatMessage>> GetByConversationIdAsync(string conversationId, CancellationToken ct = default);
    Task AddAsync(ChatMessage message, CancellationToken ct = default);
    Task DeleteByConversationIdAsync(string conversationId, CancellationToken ct = default);
    Task DeleteByConversationIdsAsync(IReadOnlyCollection<string> conversationIds, CancellationToken ct = default);
}
