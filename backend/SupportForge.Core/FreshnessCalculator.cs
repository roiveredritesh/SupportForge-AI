using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed record SourceFreshness(string Name, DateTimeOffset? LastSyncedAt, bool IsStale);

public sealed record FreshnessScore(bool IsFresh, IReadOnlyList<string> StaleSources, IReadOnlyList<SourceFreshness> Sources);

public static class FreshnessCalculator
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

    public static FreshnessScore Calculate(Project project)
    {
        var now = DateTimeOffset.UtcNow;

        bool IsStale(DateTimeOffset? lastSyncedAt) => lastSyncedAt is null || now - lastSyncedAt.Value > StaleThreshold;

        var kbSources = project.KbSources
            .Select(s => new SourceFreshness(s.Location, s.LastSyncedAt, IsStale(s.LastSyncedAt)));
        var repos = project.Repos
            .Select(r => new SourceFreshness($"{r.Owner}/{r.Repo}", r.LastSyncedAt, IsStale(r.LastSyncedAt)));

        var sources = kbSources.Concat(repos).ToList();
        var stale = sources.Where(s => s.IsStale).Select(s => s.Name).ToList();

        return new FreshnessScore(stale.Count == 0, stale, sources);
    }
}
