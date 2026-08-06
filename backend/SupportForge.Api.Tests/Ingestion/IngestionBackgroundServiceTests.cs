using Microsoft.Extensions.Logging.Abstractions;
using SupportForge.Core;
using SupportForge.Ingestion;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class IngestionBackgroundServiceTests
{
    private static (IngestionBackgroundService service, JsonFileDeadLetterRepository deadLetters, string tempDir) MakeSut(IngestionQueue queue)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var deadLetters = new JsonFileDeadLetterRepository(tempDir);
        var service = new IngestionBackgroundService(queue, deadLetters, NullLogger<IngestionBackgroundService>.Instance);
        return (service, deadLetters, tempDir);
    }

    [Fact]
    public async Task Job_Succeeding_OnFirstAttempt_RunsOnceAndMarksComplete()
    {
        var queue = new IngestionQueue();
        var job = new CountingJob("proj1", failuresBeforeSuccess: 0);
        queue.Enqueue(job);

        using var cts = new CancellationTokenSource();
        var (service, deadLetters, tempDir) = MakeSut(queue);

        await RunOneIterationAsync(service, cts);

        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(1, queue.MarkCompleteCallCount("proj1"));
        Assert.Empty(await deadLetters.GetByProjectIdAsync("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Job_FailingOnceThenSucceeding_RetriesAndMarksCompleteOnce()
    {
        var queue = new IngestionQueue();
        var job = new CountingJob("proj1", failuresBeforeSuccess: 1);
        queue.Enqueue(job);

        var (service, deadLetters, tempDir) = MakeSut(queue);
        using var cts = new CancellationTokenSource();

        // First backoff delay is ~1s, so give the retry room to happen.
        await RunOneIterationAsync(service, cts, TimeSpan.FromSeconds(2));

        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, queue.MarkCompleteCallCount("proj1"));
        Assert.Empty(await deadLetters.GetByProjectIdAsync("proj1")); // recovered -- not dead-lettered
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Job_FailingThroughEntireRetryBudget_LogsFinalFailureAndDoesNotThrow()
    {
        var queue = new IngestionQueue();
        var job = new CountingJob("proj1", failuresBeforeSuccess: int.MaxValue);
        queue.Enqueue(job);

        var (service, deadLetters, tempDir) = MakeSut(queue);
        using var cts = new CancellationTokenSource();

        // Should not throw even though the job never succeeds. Backoff delays (1s+2s+4s) mean
        // this needs a longer window than the happy-path cases.
        await RunOneIterationAsync(service, cts, TimeSpan.FromSeconds(15));

        // Default policy: 1 initial attempt + 3 retries = 4 attempts.
        Assert.Equal(4, job.AttemptCount);
        Assert.Equal(1, queue.MarkCompleteCallCount("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Job_FailingThroughEntireRetryBudget_RecordsDeadLetterEntry()
    {
        var queue = new IngestionQueue();
        var job = new CountingJob("proj1", failuresBeforeSuccess: int.MaxValue);
        queue.Enqueue(job);

        var (service, deadLetters, tempDir) = MakeSut(queue);
        using var cts = new CancellationTokenSource();

        await RunOneIterationAsync(service, cts, TimeSpan.FromSeconds(15));

        var entries = await deadLetters.GetByProjectIdAsync("proj1");
        var entry = Assert.Single(entries);
        Assert.Equal("proj1", entry.ProjectId);
        Assert.Equal(nameof(CountingJob), entry.JobType);
        Assert.Contains("Simulated transient failure", entry.Error);
        Directory.Delete(tempDir, recursive: true);
    }

    // Runs ExecuteAsync just long enough to dequeue and process a single job, then cancels the loop.
    private static async Task RunOneIterationAsync(
        IngestionBackgroundService service,
        CancellationTokenSource cts,
        TimeSpan? wait = null)
    {
        await service.StartAsync(cts.Token);
        // Give the background loop a moment to dequeue + process the single enqueued job.
        await Task.Delay(wait ?? TimeSpan.FromMilliseconds(200));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class CountingJob : IIngestionJob
    {
        private readonly int _failuresBeforeSuccess;

        public CountingJob(string projectId, int failuresBeforeSuccess)
        {
            ProjectId = projectId;
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public string ProjectId { get; }
        public int AttemptCount { get; private set; }

        public Task RunAsync(CancellationToken ct)
        {
            AttemptCount++;
            if (AttemptCount <= _failuresBeforeSuccess)
                throw new InvalidOperationException("Simulated transient failure");

            return Task.CompletedTask;
        }
    }
}
