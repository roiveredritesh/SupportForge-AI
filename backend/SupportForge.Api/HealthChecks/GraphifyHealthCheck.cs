using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SupportForge.Api.HealthChecks;

// U6: checks that the `graphify` executable is reachable on PATH via `graphify --version`,
// without invoking a real extract/update run (GraphifyCliRunner.RunAsync is for project-scoped
// jobs, not availability probing). A short timeout keeps this cheap even if the binary hangs.
public sealed class GraphifyHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo("graphify", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            if (process is null) return HealthCheckResult.Unhealthy("graphify process failed to start.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"graphify --version exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("graphify executable is not reachable on PATH.", ex);
        }
    }
}
