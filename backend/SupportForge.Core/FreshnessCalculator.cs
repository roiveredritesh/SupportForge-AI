using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed record FreshnessScore(bool IsFresh, IReadOnlyList<string> StaleSources);

public static class FreshnessCalculator
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

    public static FreshnessScore Calculate(Project project)
    {
        var now = DateTimeOffset.UtcNow;
        var staleKbSources = project.KbSources
            .Where(s => s.LastSyncedAt is null || now - s.LastSyncedAt.Value > StaleThreshold)
            .Select(s => s.Location);
        var staleRepos = project.Repos
            .Where(r => r.LastSyncedAt is null || now - r.LastSyncedAt.Value > StaleThreshold)
            .Select(r => $"{r.Owner}/{r.Repo}");

        var stale = staleKbSources.Concat(staleRepos).ToList();

        return new FreshnessScore(stale.Count == 0, stale);
    }
}
