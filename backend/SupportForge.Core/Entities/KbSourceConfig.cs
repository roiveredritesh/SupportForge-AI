namespace SupportForge.Core.Entities;

public enum KbSourceType { Documents, Confluence, Website }

/// <summary>
/// <paramref name="RepoOwner"/>/<paramref name="RepoName"/> replace the old implicit
/// "resolve against the project's first repo" behavior: when set, a <see cref="KbSourceType.Documents"/>
/// source's <paramref name="Location"/> is resolved relative to that specific repo's local clone.
/// When both are null, <paramref name="Location"/> is treated as a standalone absolute path. Unused
/// for <see cref="KbSourceType.Confluence"/> (page id) and <see cref="KbSourceType.Website"/> (URL).
/// </summary>
public sealed record KbSourceConfig(
    KbSourceType Type,
    string Location,
    DateTimeOffset? LastSyncedAt,
    string? RepoOwner = null,
    string? RepoName = null);
