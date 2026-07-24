namespace SupportForge.Core.Entities;

public sealed record FeedbackEntry(string ProjectId, string Query, bool? Useful, bool Escalated, DateTimeOffset CreatedAt);
