namespace SupportForge.Core.Entities;

public sealed record MessageSource(string Label, string Url);

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public double? Confidence { get; init; }
    public List<MessageSource>? Sources { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
