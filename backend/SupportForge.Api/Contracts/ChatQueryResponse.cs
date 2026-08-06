using SupportForge.Core.Entities;

namespace SupportForge.Api.Contracts;

public sealed class ChatQueryResponse
{
    public required string Draft { get; init; }
    public required double Confidence { get; init; }
    public required string ConversationId { get; init; }
    public IReadOnlyList<ChatSource> Sources { get; init; } = new List<ChatSource>();
}
