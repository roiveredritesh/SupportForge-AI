using System.Collections.Concurrent;

namespace SupportForge.Api.Hubs;

public interface IConversationPresenceTracker
{
    // Returns the full (distinct) list of userIds currently present in conversationId, after adding.
    IReadOnlyList<string> Join(string conversationId, string connectionId, string userId);

    // Returns the conversation the connection left plus its updated roster, or null if the
    // connection wasn't tracked (e.g. it disconnected before ever joining a conversation).
    (string ConversationId, IReadOnlyList<string> UserIds)? Leave(string connectionId);
}

// U26/U27: process-local presence roster keyed by SignalR ConnectionId, so ConversationHub can
// broadcast "who's viewing this conversation right now" to PresenceIndicator on join/leave.
// ponytail: in-memory, single-instance -- a multi-instance API deployment would need a shared
// backplane (e.g. Redis) to keep presence consistent across nodes; add if/when this API scales out.
public sealed class ConversationPresenceTracker : IConversationPresenceTracker
{
    private sealed record Entry(string ConversationId, string UserId);

    private readonly ConcurrentDictionary<string, Entry> _byConnection = new();

    public IReadOnlyList<string> Join(string conversationId, string connectionId, string userId)
    {
        _byConnection[connectionId] = new Entry(conversationId, userId);
        return UserIdsFor(conversationId);
    }

    public (string ConversationId, IReadOnlyList<string> UserIds)? Leave(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var entry)) return null;
        return (entry.ConversationId, UserIdsFor(entry.ConversationId));
    }

    private IReadOnlyList<string> UserIdsFor(string conversationId) =>
        _byConnection.Values.Where(e => e.ConversationId == conversationId).Select(e => e.UserId).Distinct().ToList();
}
