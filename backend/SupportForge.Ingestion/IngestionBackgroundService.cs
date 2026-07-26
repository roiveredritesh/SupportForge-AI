using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SupportForge.Ingestion;

public sealed class IngestionBackgroundService : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly ILogger<IngestionBackgroundService> _logger;

    public IngestionBackgroundService(IngestionQueue queue, ILogger<IngestionBackgroundService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IIngestionJob job;
            try
            {
                job = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await job.RunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion job failed for project {ProjectId}", job.ProjectId);
            }
            finally
            {
                _queue.MarkComplete(job.ProjectId);
            }
        }
    }
}
