using SupportForge.Agents.Triage;

namespace SupportForge.Agents.Guard;

/// <summary>
/// What the guard screens: the current message plus recent history, as (Role, Content) pairs.
/// ProductVersion is optional and is only read by <see cref="TurnAssessor"/>, which puts it in the combined Jev state (U17).
/// </summary>
public sealed record GuardInput(string Message, IReadOnlyList<(string Role, string Content)> History, bool HasScreenshot, string? ProductVersion = null);

/// <summary>
/// One guard's scores for the six signals. Source is "heuristic" or "jev".
/// PrecomputedIntent is set only by <see cref="TurnAssessor"/> (KTD 13): the triage answer from the same Jev request, or null when
/// that part was missing or malformed. GuardService passes it on only when the turn is not blocked.
/// </summary>
public sealed record GuardAssessment(IReadOnlyDictionary<string, double> Signals, string Source, IntentDecision? PrecomputedIntent = null);

public enum GuardVerdict { Allowed, Flagged, Blocked, Unscreened }

/// <summary>
/// Signals = merged scores used for the decision. FiredSignals = names at or above threshold, sorted ordinal.
/// Source = "off" | "heuristic" | "jev" | "jev-failed".
/// PrecomputedIntent is non-null only when the verdict is not Blocked (KTD 13): a blocked turn's triage answer is discarded.
/// </summary>
public sealed record GuardDecision(
    GuardVerdict Verdict,
    IReadOnlyDictionary<string, double> Signals,
    IReadOnlyList<string> FiredSignals,
    string Source,
    IntentDecision? PrecomputedIntent = null);

public sealed record NormalizedInput(string Text, IReadOnlyList<string> DecodedSegments);
