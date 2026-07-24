namespace SupportForge.Ingestion;

public interface IIngestionJob
{
    string ProjectId { get; }
    Task RunAsync(CancellationToken ct);
}

public interface IIngestionJobFactory
{
    IEnumerable<IIngestionJob> CreateJobs(SupportForge.Core.Entities.Project project);
}
