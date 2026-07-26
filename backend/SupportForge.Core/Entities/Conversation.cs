namespace SupportForge.Core.Entities;

public sealed class Conversation
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
