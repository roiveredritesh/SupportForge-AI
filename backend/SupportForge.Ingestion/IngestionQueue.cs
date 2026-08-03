using System.Collections.Concurrent;
using System.Threading.Channels;
using SupportForge.Agents.Tools;

namespace SupportForge.Ingestion;

public sealed class IngestionQueue : IIngestionActivity
{
    private readonly Channel<IIngestionJob> _channel = Channel.CreateUnbounded<IIngestionJob>();
    private readonly ConcurrentDictionary<string, int> _pendingByProjectId = new();
    private readonly ConcurrentDictionary<string, int> _markCompleteCallCounts = new();

    public void Enqueue(IIngestionJob job)
    {
        _pendingByProjectId.AddOrUpdate(job.ProjectId, 1, (_, count) => count + 1);
        _channel.Writer.TryWrite(job);
    }

    public ValueTask<IIngestionJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);

    public void MarkComplete(string projectId)
    {
        _pendingByProjectId.AddOrUpdate(projectId, 0, (_, count) => Math.Max(0, count - 1));
        _markCompleteCallCounts.AddOrUpdate(projectId, 1, (_, count) => count + 1);
    }

    // ponytail: test-only counter, internal + InternalsVisibleTo rather than a mocking seam.
    internal int MarkCompleteCallCount(string projectId) =>
        _markCompleteCallCounts.GetValueOrDefault(projectId);

    public bool IsBusy(string projectId) =>
        _pendingByProjectId.TryGetValue(projectId, out var count) && count > 0;

    // ponytail: no per-project "job finished" event exists, so waiting for a queued/running
    // ingestion job to clear before a delete is a short poll loop rather than a signal.
    public async Task WaitUntilIdleAsync(string projectId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy(projectId) && DateTime.UtcNow < deadline)
            await Task.Delay(200, ct);
    }
}
