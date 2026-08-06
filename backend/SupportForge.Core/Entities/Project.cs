namespace SupportForge.Core.Entities;

public sealed class Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<GitHubRepoConfig> Repos { get; init; } = new();
    public List<KbSourceConfig> KbSources { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    // Per-project override for ScheduledKbSyncService's re-sync cadence. Null falls back to the
    // global Freshness:ScheduledSyncIntervalHours default.
    public double? ScheduledSyncIntervalHours { get; init; }
}
