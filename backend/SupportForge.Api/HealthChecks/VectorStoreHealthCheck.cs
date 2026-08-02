using Microsoft.Extensions.Diagnostics.HealthChecks;
using SupportForge.VectorStore;

namespace SupportForge.Api.HealthChecks;

// U6: lightweight connectivity check -- queries a reserved collection with topK=1 rather than
// running a real search. Any exception (transport failure, service down) reports Unhealthy;
// a successful round-trip (even zero results) reports Healthy.
public sealed class VectorStoreHealthCheck : IHealthCheck
{
    private const string ProbeCollection = "__health_check__";

    private readonly IVectorStoreService _vectorStore;

    public VectorStoreHealthCheck(IVectorStoreService vectorStore)
    {
        _vectorStore = vectorStore;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Bounded timeout so a hung/unreachable store can't make /health itself slow to respond.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await _vectorStore.QueryAsync(ProbeCollection, Array.Empty<float>(), 1, ct: cts.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Vector store is unreachable.", ex);
        }
    }
}
