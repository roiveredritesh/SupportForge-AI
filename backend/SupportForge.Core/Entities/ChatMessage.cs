namespace SupportForge.Core.Entities;

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public double? Confidence { get; init; }
    // E1 (gap-closing-solutions.md Phase E): only populated on the "assistant" role message, and
    // only for branches whose verifier actually passed -- see ChatController.BuildSources.
    public IReadOnlyList<ChatSource> Sources { get; init; } = new List<ChatSource>();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
