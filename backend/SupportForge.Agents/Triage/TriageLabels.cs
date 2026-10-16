namespace SupportForge.Agents.Triage;

// U2 (JEV intent classification plan): single source of truth for intent label names, so TriageAgent,
// the classifiers and their tests share one spelling instead of repeating string literals.
public static class TriageLabels
{
    public const string KbQuestion = "kb_question";
    public const string CodeIssue = "code_issue";
    public const string CodeQuestion = "code_question";
    public const string ScreenshotError = "screenshot_error";
    public const string Greeting = "greeting";
    public const string Unclear = "unclear";

    private static readonly string[] ConcreteLabels = [KbQuestion, CodeIssue, CodeQuestion, ScreenshotError, Greeting];
    private static readonly string[] AllLabels = [.. ConcreteLabels, Unclear];

    // The five labels a classifier can actually pick, in this order. "unclear" is excluded on purpose:
    // with a confidence-reporting classifier it comes from policy, not from the model (plan KTD2).
    public static IReadOnlyList<string> Concrete => ConcreteLabels;

    // All six, Concrete order then Unclear. Also the tie-break order wherever scores tie.
    public static IReadOnlyList<string> All => AllLabels;

    // Ordinal, case-sensitive membership in All. Callers lower-case LLM output before checking (see LlmIntentClassifier).
    public static bool IsKnown(string label) => Array.IndexOf(AllLabels, label) >= 0;

    // Tie-break order for label scores: All order first, then labels outside All, ordinal among themselves.
    // Negative when `left` should win over `right`. Shared by TriageAgent (top label) and AgentContext.RunnerUpIntent
    // so both agree on which label wins a tie.
    internal static int CompareTieBreak(string left, string right)
    {
        var rank = RankOf(left).CompareTo(RankOf(right));
        return rank != 0 ? rank : string.CompareOrdinal(left, right);
    }

    private static int RankOf(string label)
    {
        var index = Array.IndexOf(AllLabels, label);
        return index >= 0 ? index : int.MaxValue;
    }
}
