using SupportForge.Core.Entities;

namespace SupportForge.Api.Contracts;

public sealed record ConversationDto(string Id, string ProjectId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChatMessageDto(
    string Id,
    string Role,
    string Content,
    double? Confidence,
    IReadOnlyList<ChatSource> Sources,
    DateTimeOffset CreatedAt);

public sealed record ConversationDetailDto(
    string Id,
    string ProjectId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChatMessageDto> Messages);

public sealed class CreateConversationRequest
{
    public required string ProjectId { get; init; }
    public string? Title { get; init; }
}
