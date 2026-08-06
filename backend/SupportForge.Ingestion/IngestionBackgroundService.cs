using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Ingestion;

public sealed class IngestionBackgroundService : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly IDeadLetterRepository _deadLetters;
    private readonly ILogger<IngestionBackgroundService> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public IngestionBackgroundService(IngestionQueue queue, IDeadLetterRepository deadLetters, ILogger<IngestionBackgroundService> logger)
    {
        _queue = queue;
        _deadLetters = deadLetters;
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
                // C7 (gap-closing-solutions.md Phase C, item 7): the retry budget above is already
                // exhausted by the time this runs -- persist so a permanently-failing job is visible
                // in the admin UI instead of only appearing in logs an operator has to go looking for.
                try
                {
                    await _deadLetters.AddAsync(
                        new DeadLetterEntry(Guid.NewGuid().ToString("n"), job.ProjectId, job.GetType().Name, ex.Message, DateTimeOffset.UtcNow),
                        stoppingToken);
                }
                catch (Exception dlEx)
                {
                    _logger.LogError(dlEx, "Failed to record dead-letter entry for project {ProjectId}", job.ProjectId);
                }
            }
            finally
            {
                _queue.MarkComplete(job.ProjectId);
            }
        }
    }
}
