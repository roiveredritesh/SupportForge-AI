namespace SupportForge.Core.Entities;

public sealed class Conversation
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // U26: users (picked from the org's employee list) invited into this one conversation for
    // scoped, temporary elevated visibility -- see ChatController's IsElevated gate and
    // ConversationHub's join-authorization check, both of which read this list. Not a role change:
    // membership here only ever grants access to this one Conversation.Id.
    public List<string> InvitedUserIds { get; init; } = new();
}
