namespace SupportForge.Agents.Guard;

public enum GuardMode { Off, Shadow, Enforce }

/// <summary>
/// Plain options object. Not bound through IOptions because SupportForge.Agents has no
/// Microsoft.Extensions.Options reference; the composition root maps configuration into it.
/// </summary>
public sealed class GuardOptions
{
    public GuardMode Mode { get; set; } = GuardMode.Off;

    /// <summary>Refuse the turn when Jev is unavailable. Only takes effect in <see cref="GuardMode.Enforce"/>.</summary>
    public bool FailClosed { get; set; } = false;

    /// <summary>Reserved; not read by the WP1 code.</summary>
    public bool CombineWithTriage { get; set; } = false;

    /// <summary>Per-signal threshold keyed by <see cref="GuardSignal.Name"/>.</summary>
    public Dictionary<string, double> Thresholds { get; set; } = new();

    public double DefaultBlockThreshold { get; set; } = 0.9;
    public double DefaultReviewThreshold { get; set; } = 0.8;

    /// <summary>Reserved for a later phase (indirect injection).</summary>
    public double IndirectThreshold { get; set; } = 0.8;

    public int HistoryTurns { get; set; } = 4;
    public int MaxStateChars { get; set; } = 8000;
    public TimeSpan JevTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan RepeatBlockWindow { get; set; } = TimeSpan.FromMinutes(10);
    public int RepeatBlockCount { get; set; } = 3;

    public double ThresholdFor(GuardSignal signal)
    {
        if (Thresholds.TryGetValue(signal.Name, out var threshold)) return threshold;
        return signal.Class == GuardSignalClass.Block ? DefaultBlockThreshold : DefaultReviewThreshold;
    }
}
