using SupportForge.Core.Entities;

namespace SupportForge.Ingestion;

public interface IIngestionJob
{
    string ProjectId { get; }
    Task RunAsync(CancellationToken ct);
}

public interface IIngestionJobFactory
{
    // U7: triggeredByUserId is the admin who clicked Trigger Ingestion/Force Reindex (available
    // from the authenticated request), or null for a scheduled/webhook-triggered run that has no
    // request-bound identity to attribute. Threaded through to KbVectorIndexer's TokenUsageEntry
    // writes so interactive ingestion runs are attributable the same way chat queries are.
    IEnumerable<IIngestionJob> CreateJobs(Project project, string? triggeredByUserId);
}
