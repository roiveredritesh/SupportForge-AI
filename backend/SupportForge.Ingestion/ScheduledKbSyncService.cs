using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;

namespace SupportForge.Ingestion;

/// <summary>
/// C4: periodic re-ingestion for KB sources that have no push mechanism of their own (Documents,
/// Confluence, Website) -- closes the "no scheduled re-ingestion" gap from the Gap Analysis. Code
/// repos stay webhook-driven (see WebhooksController); re-cloning them on a fixed timer too would be
/// redundant with -- and much more expensive than -- the push-triggered path.
///
/// Runs a short, fixed poll loop (Freshness:PollIntervalMinutes) rather than one timer per project's
/// own cadence: on each poll, every project's actual due-time is computed from its own
/// <see cref="Project.ScheduledSyncIntervalHours"/> (or the global Freshness:ScheduledSyncIntervalHours
/// default) against its oldest KB source LastSyncedAt. This gives true per-project cadence without
/// standing up N separate timers.
/// </summary>
public sealed class ScheduledKbSyncService : BackgroundService
{
    private readonly IProjectRepository _projects;
    private readonly IngestionQueue _queue;
    private readonly DocumentIngestionJobFactory _docFactory;
    private readonly TimeSpan _pollInterval;
    private readonly double _defaultIntervalHours;
    private readonly ILogger<ScheduledKbSyncService> _logger;

    public ScheduledKbSyncService(
        IProjectRepository projects, IngestionQueue queue, DocumentIngestionJobFactory docFactory,
        IConfiguration configuration, ILogger<ScheduledKbSyncService> logger)
    {
        _projects = projects;
        _queue = queue;
        _docFactory = docFactory;
        _defaultIntervalHours = configuration.GetValue<double?>("Freshness:ScheduledSyncIntervalHours") ?? 24;
        // Deliberately much shorter than any realistic sync interval -- this is how often the
        // service *checks* whether a project is due, not how often it actually re-syncs one.
        var pollMinutes = configuration.GetValue<double?>("Freshness:PollIntervalMinutes") ?? 15;
        _pollInterval = TimeSpan.FromMinutes(pollMinutes);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        // Fires once immediately on startup, then every _pollInterval -- matches
        // IngestionBackgroundService's "just start working" shape rather than waiting a full
        // interval before the first check.
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // internal + InternalsVisibleTo (see IngestionQueue's matching comment) so tests can drive one
    // tick directly instead of racing PeriodicTimer / BackgroundService's fire-and-forget start.
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var all = await _projects.GetAllAsync(ct);
        var enqueued = 0;
        foreach (var project in all)
        {
            if (project.KbSources.Count == 0) continue;
            if (!IsDue(project, now)) continue;
            if (_queue.IsBusy(project.Id))
            {
                _logger.LogInformation("ScheduledKbSyncService: skipping project {ProjectId} -- ingestion already running", project.Id);
                continue;
            }

            foreach (var job in _docFactory.CreateJobs(project))
                _queue.Enqueue(job);
            enqueued++;
        }

        _logger.LogInformation("ScheduledKbSyncService: enqueued KB re-sync for {Count} project(s)", enqueued);
    }

    private bool IsDue(Project project, DateTimeOffset now)
    {
        // A never-synced source sorts first (MinValue), so any project with at least one
        // never-synced KB source is always due.
        var oldest = project.KbSources.Select(s => s.LastSyncedAt).OrderBy(d => d ?? DateTimeOffset.MinValue).First();
        if (oldest is null) return true;

        var interval = TimeSpan.FromHours(project.ScheduledSyncIntervalHours ?? _defaultIntervalHours);
        return now - oldest.Value >= interval;
    }
}
