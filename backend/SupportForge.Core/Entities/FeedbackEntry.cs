namespace SupportForge.Core.Entities;

// U17: small fixed reason-code set for "not useful" feedback -- required on Useful==false,
// optional/absent otherwise (FeedbackController enforces the requirement).
public enum FeedbackReasonCode { Irrelevant, WrongVersion, Incomplete, Other }

// U5: UserId stamps who submitted feedback, avoiding a backfill migration when Sprint 4 reworks
// feedback. Nullable so older, pre-existing entries (written before this field existed) still
// deserialize.
// U17: Sources (the ChatSource.Url values shown alongside the answer this feedback is about) and
// ReasonCode drive retrieval down-weighting -- KbSearchTool counts negative votes per source. Both
// nullable/optional so pre-Sprint-4 entries still deserialize.
public sealed record FeedbackEntry(
    string ProjectId,
    string Query,
    bool? Useful,
    bool Escalated,
    DateTimeOffset CreatedAt,
    string? UserId = null,
    IReadOnlyList<string>? Sources = null,
    FeedbackReasonCode? ReasonCode = null);
