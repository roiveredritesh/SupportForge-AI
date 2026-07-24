namespace SupportForge.Core.Entities;

public sealed record TokenUsageEntry(string ProjectId, int TotalTokens, DateTimeOffset CreatedAt);
