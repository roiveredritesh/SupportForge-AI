using SupportForge.Agents.Guard;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

/// <summary>Manually advanced clock for window-based guard tests.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

public class GuardRepeatTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static GuardOptions Options() => new() { RepeatBlockCount = 3, RepeatBlockWindow = TimeSpan.FromMinutes(10) };

    [Fact]
    public void RecordBlock_ReturnsTrueExactlyOnTheNthBlockInWindow()
    {
        var time = new FakeTimeProvider(Start);
        var tracker = new GuardRepeatTracker(time);
        var options = Options();

        Assert.False(tracker.RecordBlock("user-1", options));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.RecordBlock("user-1", options));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.RecordBlock("user-1", options));
    }

    [Fact]
    public void RecordBlock_ReturnsFalseAfterThresholdIsReachedInSameWindow()
    {
        var time = new FakeTimeProvider(Start);
        var tracker = new GuardRepeatTracker(time);
        var options = Options();

        tracker.RecordBlock("user-1", options);
        tracker.RecordBlock("user-1", options);
        Assert.True(tracker.RecordBlock("user-1", options));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.RecordBlock("user-1", options));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.RecordBlock("user-1", options));
    }

    [Fact]
    public void RecordBlock_ResetsOnceTheWindowHasPassed()
    {
        var time = new FakeTimeProvider(Start);
        var tracker = new GuardRepeatTracker(time);
        var options = Options();

        tracker.RecordBlock("user-1", options);
        tracker.RecordBlock("user-1", options);
        Assert.True(tracker.RecordBlock("user-1", options));

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(tracker.RecordBlock("user-1", options));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.RecordBlock("user-1", options));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.RecordBlock("user-1", options));
    }

    [Fact]
    public void RecordBlock_TracksUsersIndependently()
    {
        var time = new FakeTimeProvider(Start);
        var tracker = new GuardRepeatTracker(time);
        var options = Options();

        Assert.False(tracker.RecordBlock("user-a", options));
        Assert.False(tracker.RecordBlock("user-a", options));
        Assert.True(tracker.RecordBlock("user-a", options));

        Assert.False(tracker.RecordBlock("user-b", options));
        Assert.False(tracker.RecordBlock("user-b", options));
        Assert.True(tracker.RecordBlock("user-b", options));
    }
}
