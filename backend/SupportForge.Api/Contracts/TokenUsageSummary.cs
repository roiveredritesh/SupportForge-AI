namespace SupportForge.Api.Contracts;

public sealed record TokenUsageSummary(int Total, IReadOnlyDictionary<string, int> BySource);

// U6: org-wide token usage (OrgsController.GetOrgTokenUsage) -- additive sibling to
// TokenUsageSummary above, not a replacement. Chart reuses QueryVolumePoint's day-bucketed shape;
// grid entries deliberately omit Config (KTD11) since it's not needed for the grid and may carry
// arbitrary per-query metadata. ProjectIds is the distinct set present in the org's
// unfiltered-by-projectId result, so the frontend can build its project filter from org data
// (KTD10), not from the caller's own project memberships.
public sealed record OrgTokenUsageEntry(string ProjectId, string? UserId, int TotalTokens, DateTimeOffset CreatedAt, string Source);

public sealed record OrgTokenUsage(
    IReadOnlyList<QueryVolumePoint> Chart,
    IReadOnlyList<OrgTokenUsageEntry> Entries,
    IReadOnlyList<string> ProjectIds);
