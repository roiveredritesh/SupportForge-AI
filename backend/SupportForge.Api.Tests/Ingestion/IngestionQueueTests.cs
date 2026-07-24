using SupportForge.Ingestion;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class IngestionQueueTests
{
    [Fact]
    public async Task Enqueue_Then_DequeueAsync_ReturnsSameJob()
    {
        var queue = new IngestionQueue();
        var ran = false;
        var job = new FakeJob("proj1", () => ran = true);

        queue.Enqueue(job);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        await dequeued.RunAsync(CancellationToken.None);

        Assert.Equal("proj1", dequeued.ProjectId);
        Assert.True(ran);
    }

    private sealed class FakeJob : IIngestionJob
    {
        private readonly Action _onRun;
        public FakeJob(string projectId, Action onRun) { ProjectId = projectId; _onRun = onRun; }
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) { _onRun(); return Task.CompletedTask; }
    }
}
