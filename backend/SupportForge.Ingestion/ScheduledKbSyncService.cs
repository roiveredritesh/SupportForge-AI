using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportForge.Core;
using SupportForge.Ingestion.Documents;

namespace SupportForge.Ingestion;

/// <summary>
/// C4: periodic re-ingestion for KB sources that have no push mechanism of their own (Documents,
/// Confluence, Website) -- closes the "no scheduled re-ingestion" gap from the Gap Analysis. Code
/// repos stay webhook-driven (see WebhooksController); re-cloning them on a fixed timer too would be
/// redundant with -- and much more expensive than -- the push-triggered path.
/// </summary>
public sealed class ScheduledKbSyncService : BackgroundService
{
    private readonly IProjectRepository _projects;
    private readonly IngestionQueue _queue;
    private readonly DocumentIngestionJobFactory _docFactory;
    private readonly TimeSpan _interval;
    private readonly ILogger<ScheduledKbSyncService> _logger;

    public ScheduledKbSyncService(
        IProjectRepository projects, IngestionQueue queue, DocumentIngestionJobFactory docFactory,
        IConfiguration configuration, ILogger<ScheduledKbSyncService> logger)
    {
        _projects = projects;
        _queue = queue;
        _docFactory = docFactory;
        var hours = configuration.GetValue<double?>("Freshness:ScheduledSyncIntervalHours") ?? 24;
        _interval = TimeSpan.FromHours(hours);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        // Fires once immediately on startup, then every _interval -- matches IngestionBackgroundService's
        // "just start working" shape rather than waiting a full interval before the first sync.
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
        var all = await _projects.GetAllAsync(ct);
        var enqueued = 0;
        foreach (var project in all)
        {
            if (project.KbSources.Count == 0) continue;
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
}
