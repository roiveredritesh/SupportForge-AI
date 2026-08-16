namespace SupportForge.Core.Entities;

// Source distinguishes where the tokens were spent -- "chat" (agent pipeline, existing usage) vs
// "ingestion" (KbVectorIndexer's embedding calls during register/reindex) -- so a project can show
// a breakdown, not just one opaque total.
// U10: ProductVersion/Config are optional trailing fields, piggybacking on the per-query entry
// this sprint's chat path already writes (ChatController) -- reuses existing storage for
// version-aware analytics instead of standing up a new query-log entity.
// U6: UserId (same trailing-optional-field convention) is the authenticated caller's ID for chat
// writes; null for background-triggered ingestion writes (U7).
public sealed record TokenUsageEntry(
    string ProjectId, int TotalTokens, DateTimeOffset CreatedAt, string Source,
    string? ProductVersion = null, IReadOnlyDictionary<string, string>? Config = null,
    string? UserId = null);
