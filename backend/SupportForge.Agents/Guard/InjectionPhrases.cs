using System.Text.RegularExpressions;

namespace SupportForge.Agents.Guard;

/// <summary>
/// Shared phrase lists. Ingestion (caption sanitising) and runtime screening both read from here so
/// the two can't drift apart.
/// </summary>
public static class InjectionPhrases
{
    // Moved verbatim from IngestionImageCaptioner. Caption sanitising behaviour must not change.
    public static readonly Regex CaptionFilter = new(
        "ignore (all )?(previous|prior) instructions|disregard the above|you are now|^system:|new instructions:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // Each pattern maps to exactly one signal. Patterns are kept high-precision on purpose: a false
    // positive here only flags the turn, but a noisy heuristic would erode trust in the review queue.
    private static readonly IReadOnlyList<(string Signal, Regex Pattern)> HeuristicPatterns =
    [
        (GuardSignals.OverrideInstructions, new Regex(
            @"\b(ignore|disregard|forget)\b.{0,20}\b(all\s+)?(previous|prior|above|earlier|your)\s+(instructions|rules|prompts?|directions)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GuardSignals.OverrideInstructions, new Regex(
            @"\byou are now\b.{0,40}\b(no longer|not bound|free|unrestricted|without (rules|restrictions))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GuardSignals.OverrideInstructions, new Regex(
            @"\bnew instructions\s*:",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),

        (GuardSignals.ExtractSystemPrompt, new Regex(
            @"\b(reveal|show|print|repeat|output|display|tell me)\b.{0,30}\b(your|the)\s+(system\s+prompt|hidden\s+(instructions|prompt)|initial\s+(instructions|prompt))\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GuardSignals.ExtractSystemPrompt, new Regex(
            @"\brepeat (the )?(text|words|everything) above\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),

        // DAN is matched case-sensitively: "dan" is a common first name in support mail.
        (GuardSignals.JailbreakRoleplay, new Regex(
            @"\bDAN\b|\b[Dd]o [Aa]nything [Nn]ow\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        // "developer mode" is deliberately absent: real products have one and customers ask about it.
        (GuardSignals.JailbreakRoleplay, new Regex(
            @"\b(god|jailbreak|unrestricted)\s+mode\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),

        (GuardSignals.CrossTenantProbe, new Regex(
            @"\b(other|another|different)\s+(customers?|companies|company|organi[sz]ations?|tenants?)('s|')?\s+(data|tickets|conversations|messages|projects|records)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    public static IReadOnlyList<(string Signal, Regex Pattern)> Heuristics => HeuristicPatterns;
}
