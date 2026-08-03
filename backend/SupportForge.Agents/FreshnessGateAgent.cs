using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Tools;
using SupportForge.Core;

namespace SupportForge.Agents;

/// <summary>
/// WS3 (retrieval-pipeline remediation plan): runs right after Triage, before the KB/Code/Vision
/// fan-out, so every branch (and Drafter) can see staleness before retrieval runs rather than as an
/// unused post-hoc calculation. Combines two signals that catch different failure modes:
/// <see cref="IIngestionActivity.IsBusy"/> (a sync is running right now -- minutes-scale) and
/// <see cref="FreshnessCalculator"/> (no source has synced in 7+ days -- day-scale). Both are local/
/// in-memory checks, no LLM call, so this is cheap enough to run unconditionally.
/// </summary>
public sealed class FreshnessGateAgent : IAgent
{
    private readonly IProjectRepository _projects;
    private readonly IIngestionActivity _ingestionActivity;
    private readonly ILogger<FreshnessGateAgent> _logger;
    public string Name => "FreshnessGate";

    public FreshnessGateAgent(IProjectRepository projects, IIngestionActivity ingestionActivity, ILogger<FreshnessGateAgent> logger)
    {
        _projects = projects;
        _ingestionActivity = ingestionActivity;
        _logger = logger;
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId}", Name, context.ProjectId);
        try
        {
            var project = await _projects.GetByIdAsync(context.ProjectId, ct);
            var syncInProgress = _ingestionActivity.IsBusy(context.ProjectId);
            // A project with no sources yet has nothing stale to report -- IsFresh: true, not a false alarm.
            var score = project is null ? new FreshnessScore(true, [], []) : FreshnessCalculator.Calculate(project);
            context.Freshness = new FreshnessContext(syncInProgress, score);

            _logger.LogInformation(
                "{Agent} completed in {ElapsedMs}ms: syncInProgress={SyncInProgress} isFresh={IsFresh}",
                Name, sw.ElapsedMilliseconds, syncInProgress, score.IsFresh);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
