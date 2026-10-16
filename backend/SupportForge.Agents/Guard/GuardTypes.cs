namespace SupportForge.Agents.Guard;

/// <summary>What the guard screens: the current message plus recent history, as (Role, Content) pairs.</summary>
public sealed record GuardInput(string Message, IReadOnlyList<(string Role, string Content)> History, bool HasScreenshot);

/// <summary>One guard's scores for the six signals. Source is "heuristic" or "jev".</summary>
public sealed record GuardAssessment(IReadOnlyDictionary<string, double> Signals, string Source);

public enum GuardVerdict { Allowed, Flagged, Blocked, Unscreened }

/// <summary>
/// Signals = merged scores used for the decision. FiredSignals = names at or above threshold, sorted ordinal.
/// Source = "off" | "heuristic" | "jev" | "jev-failed".
/// </summary>
public sealed record GuardDecision(
    GuardVerdict Verdict,
    IReadOnlyDictionary<string, double> Signals,
    IReadOnlyList<string> FiredSignals,
    string Source);

public sealed record NormalizedInput(string Text, IReadOnlyList<string> DecodedSegments);
