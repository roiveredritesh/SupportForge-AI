using System.Collections.Concurrent;

namespace SupportForge.Agents.Guard;

/// <summary>
/// Counts recent blocks per user so that repeated attempts surface in the ops log. Only logs and
/// alerts; it never suspends or rate-limits anyone (R25).
/// </summary>
public sealed class GuardRepeatTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _blocksByUser = new(StringComparer.Ordinal);

    public GuardRepeatTracker(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Records one block and returns true only on the call that brings the in-window count to
    /// <see cref="GuardOptions.RepeatBlockCount"/>. Later blocks in the same window return false.
    /// </summary>
    public bool RecordBlock(string userId, GuardOptions options)
    {
        var now = _timeProvider.GetUtcNow();
        var queue = _blocksByUser.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());

        // Timestamps are appended in time order, so pruning from the front is enough.
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > options.RepeatBlockWindow)
            {
                queue.Dequeue();
            }

            queue.Enqueue(now);
            return queue.Count == options.RepeatBlockCount;
        }
    }
}
