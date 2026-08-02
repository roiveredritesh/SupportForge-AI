using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace SupportForge.Ingestion;

public sealed class IngestionBackgroundService : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly ILogger<IngestionBackgroundService> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public IngestionBackgroundService(IngestionQueue queue, ILogger<IngestionBackgroundService> logger)
    {
        _queue = queue;
        _logger = logger;
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "Ingestion job retry attempt {AttemptNumber} after failure",
                        args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();
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
                await _retryPipeline.ExecuteAsync(
                    static async (job, ct) => await job.RunAsync(ct),
                    job,
                    stoppingToken);
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
