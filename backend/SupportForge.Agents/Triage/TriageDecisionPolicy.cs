namespace SupportForge.Agents.Triage;

// The intent chosen by the policy, and the rule that decided it. AppliedRule is logged so each override is visible
// in traces: "top" | "greeting-has-question" | "screenshot-without-image" | "code-pair-by-malfunction" |
// "low-confidence" | "small-margin" | "unknown-label".
public sealed record TriagePolicyResult(string Intent, string AppliedRule);

// U5 (JEV intent classification plan), R4: the deterministic step that turns label scores into the intent TriageAgent acts on.
// It is a pure function, so every rule is covered by a table-driven test. Rules run in order and the first match decides.
// Rules 1 and 2 replace the top label and then continue to rule 4, so a signal can't skip the confidence and margin
// gate. An LLM decision is one label at 1.0 with confidence 1.0 and no signals, so it always passes through unchanged.
public static class TriageDecisionPolicy
{
    // A Noul answer counts as "yes" at or above 0.5 (U5 step 3).
    private const double SignalYes = 0.5;

    public static TriagePolicyResult Decide(IntentDecision decision, bool hasScreenshot, TriageOptions options)
    {
        var ranked = Rank(decision);
        if (ranked.Count == 0)
        {
            return Unclear("unknown-label");
        }

        var top = ranked[0];

        // Rule 0: a label outside TriageLabels.All can't be routed, so it is treated as unclear.
        if (!TriageLabels.IsKnown(top))
        {
            return Unclear("unknown-label");
        }

        // Rule 0b: an LLM decision can pick "unclear" itself. That is already the answer.
        if (top == TriageLabels.Unclear)
        {
            return Unclear("top");
        }

        // Set when rule 1 or 2 replaces the top label. The replaced label is then left out of the runner-up, so the
        // margin check compares the new top only with labels that are still in play.
        string? eliminated = null;
        string? appliedRule = null;
        var pairExempt = false;

        // A missing or NaN signal counts as absent, so rules that need it are skipped.
        // refers_to_image < 0.5 with no screenshot attached means the "screenshot" label can't be answered (rule 2).
        var screenshotUnusable = !hasScreenshot && SignalBelow(decision, TriageSignals.RefersToImage, SignalYes);

        if (top == TriageLabels.Greeting && SignalAtLeast(decision, TriageSignals.HasSupportQuestion, SignalYes))
        {
            // Rule 1: "thanks, and how do I reset my password?" is a support question, not a pleasantry.
            // Take the best label that is not greeting. A screenshot label is excluded when no image is usable (see above),
            // so the policy can't end up on screenshot_error with nothing to look at.
            var candidate = FirstCandidate(ranked, label =>
                label != TriageLabels.Greeting && !(label == TriageLabels.ScreenshotError && screenshotUnusable));
            if (candidate is null)
            {
                return Unclear("greeting-has-question");
            }

            top = candidate;
            eliminated = TriageLabels.Greeting;
            appliedRule = "greeting-has-question";
        }
        else if (top == TriageLabels.ScreenshotError && screenshotUnusable)
        {
            // Rule 2: the question is about a screenshot that was not attached. Take the best label that is not
            // screenshot_error.
            var candidate = FirstCandidate(ranked, label => label != TriageLabels.ScreenshotError);
            if (candidate is null)
            {
                return Unclear("screenshot-without-image");
            }

            top = candidate;
            eliminated = TriageLabels.ScreenshotError;
            appliedRule = "screenshot-without-image";
        }
        else if (top is TriageLabels.CodeIssue or TriageLabels.CodeQuestion)
        {
            // Rule 3: code_issue and code_question route to the same branches. They differ only in the Drafter's
            // instructions, so a close call between them is settled by reports_malfunction, not sent to "unclear".
            // Only applies when the other code label is the runner-up within MinMargin.
            var other = top == TriageLabels.CodeIssue ? TriageLabels.CodeQuestion : TriageLabels.CodeIssue;
            if (RunnerUp(ranked, top, eliminated) == other
                && Math.Abs(Probability(decision, top) - Probability(decision, other)) < options.MinMargin)
            {
                // Exempt from rule 4's margin check, but still subject to its confidence check below.
                pairExempt = true;

                // With no reports_malfunction answer the top label is kept as it is.
                if (TryGetSignal(decision, TriageSignals.ReportsMalfunction, out var malfunction))
                {
                    top = malfunction >= SignalYes ? TriageLabels.CodeIssue : TriageLabels.CodeQuestion;
                    appliedRule = "code-pair-by-malfunction";
                }
            }
        }

        // Rule 4: confidence first, then the margin against the best label still in play.
        // Written as !(x >= y) so a NaN confidence or margin fails closed to "unclear".
        if (!(decision.Confidence >= options.MinConfidence))
        {
            return Unclear("low-confidence");
        }

        if (!pairExempt)
        {
            var runnerUp = RunnerUp(ranked, top, eliminated);
            var margin = Probability(decision, top) - (runnerUp is null ? 0 : Probability(decision, runnerUp));
            if (!(margin >= options.MinMargin))
            {
                return Unclear("small-margin");
            }
        }

        // Rule 5: the top label stands.
        return new TriagePolicyResult(top, appliedRule ?? "top");
    }

    // Labels by probability, highest first. Ties follow TriageLabels.All, the same order TriageAgent and AgentContext use.
    private static IReadOnlyList<string> Rank(IntentDecision decision) =>
        decision.Scores.Keys
            .OrderByDescending(label => Probability(decision, label))
            .ThenBy(label => label, Comparer<string>.Create((left, right) => TriageLabels.CompareTieBreak(left, right)))
            .ToList();

    // A NaN probability counts as 0, so it can't win a comparison or pass a margin check.
    private static double Probability(IntentDecision decision, string label) =>
        decision.Scores.TryGetValue(label, out var probability) && !double.IsNaN(probability) ? probability : 0;

    // The best-ranked label other than `top` and `eliminated`, or null when none is left.
    private static string? RunnerUp(IReadOnlyList<string> ranked, string top, string? eliminated)
    {
        foreach (var label in ranked)
        {
            if (label != top && label != eliminated)
            {
                return label;
            }
        }

        return null;
    }

    // The best-ranked label that is a known label other than unclear, and that `allowed` accepts.
    private static string? FirstCandidate(IReadOnlyList<string> ranked, Func<string, bool> allowed)
    {
        foreach (var label in ranked)
        {
            if (TriageLabels.IsKnown(label) && label != TriageLabels.Unclear && allowed(label))
            {
                return label;
            }
        }

        return null;
    }

    private static bool TryGetSignal(IntentDecision decision, string name, out double value)
    {
        if (decision.Signals.TryGetValue(name, out value) && !double.IsNaN(value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool SignalAtLeast(IntentDecision decision, string name, double threshold) =>
        TryGetSignal(decision, name, out var value) && value >= threshold;

    private static bool SignalBelow(IntentDecision decision, string name, double threshold) =>
        TryGetSignal(decision, name, out var value) && value < threshold;

    private static TriagePolicyResult Unclear(string appliedRule) => new(TriageLabels.Unclear, appliedRule);
}
