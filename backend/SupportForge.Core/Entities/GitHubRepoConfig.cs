namespace SupportForge.Core.Entities;

// No per-repo credential field -- GitRepoSyncService.ResolveTokenAsync only ever checks the org's
// connected MCP server, then the global GitHub:Token config.
public sealed record GitHubRepoConfig(string Owner, string Repo, string DefaultBranch, DateTimeOffset? LastSyncedAt = null);
