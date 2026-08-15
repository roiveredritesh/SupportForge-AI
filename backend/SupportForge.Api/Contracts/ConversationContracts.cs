using SupportForge.Core.Entities;

namespace SupportForge.Api.Contracts;

public sealed record ConversationDto(
    string Id, string ProjectId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<string> InvitedUserIds);

public sealed record ChatMessageDto(
    string Id,
    string Role,
    string Content,
    double? Confidence,
    IReadOnlyList<ChatSource> Sources,
    DateTimeOffset CreatedAt,
    int? TotalTokensUsed);

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

// U26: UserId is picked from the org's employee list (GET /api/orgs/{orgId}/employees) --
// no separate contact/email system.
public sealed record InviteToConversationRequest(string UserId);
