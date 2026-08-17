namespace SupportForge.Core.Entities;

public sealed class Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    // Sprint 0 (U2): backfilled on startup for pre-existing projects by ProjectOrgMigration.
    public string? OrgId { get; init; }
    public List<GitHubRepoConfig> Repos { get; init; } = new();
    public List<KbSourceConfig> KbSources { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    // Per-project override for ScheduledKbSyncService's re-sync cadence. Null falls back to the
    // global Freshness:ScheduledSyncIntervalHours default.
    public double? ScheduledSyncIntervalHours { get; init; }

    // Per-project opt-in for the code-graph Tier 2 LLM classification stage -- see
    // Org.CodeClassificationEnabled for the org-wide master switch this is AND'd with. Default
    // false: classification stays off for a project until an admin explicitly turns it on here too.
    public bool CodeClassificationEnabled { get; init; }
}
