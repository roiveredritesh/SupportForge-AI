namespace SupportForge.Core.Entities;

// Source distinguishes where the tokens were spent -- "chat" (agent pipeline, existing usage) vs
// "ingestion" (KbVectorIndexer's embedding calls during register/reindex) -- so a project can show
// a breakdown, not just one opaque total.
public sealed record TokenUsageEntry(string ProjectId, int TotalTokens, DateTimeOffset CreatedAt, string Source);
