namespace SupportForge.Api.Contracts;

public sealed record TokenUsageSummary(int Total, IReadOnlyDictionary<string, int> BySource);
