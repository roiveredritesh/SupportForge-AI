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
    // Only populated on the "assistant" role message -- total LLM tokens spent by the whole
    // pipeline (Triage + KB/Code/Vision verifiers + Drafter) answering this one turn.
    public int? TotalTokensUsed { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // U19: full-detail pipeline state, only populated on the "assistant" role message, captured
    // regardless of the asking user's role (same "pipeline always computes full detail" precedent
    // as CodeDetails/CommitHistory gating in ChatQueryResponse -- role only gates what the chat
    // response *shows*, not what gets persisted here). EscalationsController reads these fields to
    // build the handoff Markdown without re-running the agent pipeline.
    public IReadOnlyList<string> KbSnippets { get; init; } = new List<string>();
    public IReadOnlyList<string> CodeSnippets { get; init; } = new List<string>();
    public string? VisionFindings { get; init; }
    public string? ProductVersion { get; init; }
    public IReadOnlyDictionary<string, string>? Config { get; init; }
}
