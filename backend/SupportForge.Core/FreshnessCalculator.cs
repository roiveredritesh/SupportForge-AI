using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed record FreshnessScore(bool IsFresh, IReadOnlyList<string> StaleSources);

public static class FreshnessCalculator
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

    public static FreshnessScore Calculate(Project project)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = project.KbSources
            .Where(s => s.LastSyncedAt is null || now - s.LastSyncedAt.Value > StaleThreshold)
            .Select(s => s.Location)
            .ToList();

        return new FreshnessScore(stale.Count == 0, stale);
    }
}
