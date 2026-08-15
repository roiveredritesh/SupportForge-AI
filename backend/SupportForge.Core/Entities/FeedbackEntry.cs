namespace SupportForge.Core.Entities;

// U5: UserId stamps who submitted feedback, avoiding a backfill migration when Sprint 4 reworks
// feedback. Nullable so older, pre-existing entries (written before this field existed) still
// deserialize.
public sealed record FeedbackEntry(string ProjectId, string Query, bool? Useful, bool Escalated, DateTimeOffset CreatedAt, string? UserId = null);
