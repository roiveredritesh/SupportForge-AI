using System.Text.Json.Serialization;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Contracts;

public sealed class ChatQueryResponse
{
    public required string Draft { get; init; }
    public required double Confidence { get; init; }
    public required string ConversationId { get; init; }
    public IReadOnlyList<ChatSource> Sources { get; init; } = new List<ChatSource>();
    public int TotalTokensUsed { get; init; }

    // U6: CodeAnalyzerAgent's raw findings (paths/line ranges/snippets, as accumulated in
    // AgentContext.CodeSnippets), gated to L2/L3/Admin by ChatController -- L1 callers must never
    // see this field at all (omitted, not null-but-present), hence WhenWritingNull.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CodeDetails { get; init; }
}
