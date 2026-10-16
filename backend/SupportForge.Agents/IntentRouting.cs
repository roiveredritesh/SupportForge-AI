using SupportForge.Agents.Triage;

namespace SupportForge.Agents;

// U7: the single place that decides which retrieval branches an intent needs. The KB and Code
// retrieval gates (KbResearcher, KbResearcherVerifier, CrossReference, CodeAnalyzer,
// CodeAnalyzerVerifier) all ask here instead of repeating inline intent lists, so the mapping from
// label to branch lives in one spot.
public static class IntentRouting
{
    // Primary-intent check. With secondary routing disabled (the default) these two calls are the
    // whole decision, and they match the inline gates they replaced exactly.
    public static bool NeedsKb(AgentContext context) =>
        LabelNeedsKb(context.Intent) || SecondaryNeeds(context, LabelNeedsKb);

    public static bool NeedsCode(AgentContext context) =>
        LabelNeedsCode(context.Intent) || SecondaryNeeds(context, LabelNeedsCode);

    private static bool LabelNeedsKb(string label) =>
        label is TriageLabels.KbQuestion or TriageLabels.CodeIssue or TriageLabels.CodeQuestion;

    private static bool LabelNeedsCode(string label) =>
        label is TriageLabels.CodeIssue or TriageLabels.CodeQuestion;

    // Opt-in secondary-intent rule. A confident runner-up intent can ADD a branch. It is OR-ed onto
    // the primary check above, so it can only widen retrieval and never removes a branch the primary
    // intent already needs. unclear and greeting are excluded as primaries because they carry no
    // retrieval intent of their own; a runner-up there would be noise, not a signal.
    private static bool SecondaryNeeds(AgentContext context, Func<string, bool> predicate)
    {
        if (!context.SecondaryIntentEnabled)
        {
            return false;
        }

        if (context.Intent is TriageLabels.Unclear or TriageLabels.Greeting)
        {
            return false;
        }

        if (context.RunnerUpIntent is not { } runnerUp)
        {
            return false;
        }

        if (runnerUp.Probability < context.SecondaryMinProbability)
        {
            return false;
        }

        return predicate(runnerUp.Label);
    }
}
