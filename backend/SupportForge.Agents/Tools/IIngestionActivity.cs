namespace SupportForge.Agents.Tools;

/// <summary>
/// WS3 (retrieval-pipeline remediation plan): real-time "is a sync running right now" signal for
/// <see cref="FreshnessGateAgent"/>. Interface lives here (not alongside its implementation) for the
/// same reason as <see cref="IGraphifyQueryTool"/> -- the concrete implementation is
/// <c>SupportForge.Ingestion.IngestionQueue</c>, which this project doesn't reference.
/// </summary>
public interface IIngestionActivity
{
    bool IsBusy(string projectId);
}
