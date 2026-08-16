namespace SupportForge.Api.Contracts;

// U4: Dashboard metric shapes -- one record per new ProjectsController endpoint.
public sealed record QueryVolumePoint(string Date, int Count);

public sealed record FeedbackSummary(int Useful, int NotUseful);

public sealed record EscalationStats(int Open, int Claimed, int Resolved);
