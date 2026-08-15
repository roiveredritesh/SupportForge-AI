namespace SupportForge.Core.Entities;

public enum EscalationStatus { Open, Claimed, Resolved }

// U19: a conversation handed off to L2/L3, with the handoff Markdown assembled ONCE at escalation
// time (from the already-computed AgentContext state cached on the triggering ChatMessage -- see
// EscalationsController) and cached here. No re-running the agent pipeline on claim/view.
// ProjectId is denormalized off the conversation (same pattern as FeedbackEntry.ProjectId) so
// GET /api/escalations can scope by project membership without a join.
public sealed record Escalation(
    string Id,
    string ConversationId,
    string ProjectId,
    string EscalatedByUserId,
    DateTimeOffset EscalatedAt,
    string Markdown,
    EscalationStatus Status,
    string? ClaimedByUserId = null,
    DateTimeOffset? ClaimedAt = null,
    DateTimeOffset? ResolvedAt = null);
