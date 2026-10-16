namespace SupportForge.Agents.Verification;

// U11 (JEV intent classification plan, R13 / KTD 9): the one seam every findings judge goes through. The
// KB, Code and Vision verifiers and the Drafter's groundedness check all ask an IFindingsJudge instead of
// calling the LLM directly. LlmFindingsJudge is the behaviour-preserving implementation; the Jev judge
// (U12) implements the same interface without an LLM, so the verifiers never learn which judge decided.
public enum FindingsKind { Kb, Code }

// BestIndex is the 0-based index of the single snippet that answers the question, or null for "none".
// Scores and Coverage are for judges that produce them (the Jev judge); the LLM judge leaves both null.
// Source names the judge that decided ("llm", later "jev").
// U18 (R26): InjectionScores is the per-snippet indirect-injection score 0..1, aligned with the snippets the judge was given.
// Only the Jev judge sets it. The LLM judge, the fallback and Shadow leave it null, which means "not screened".
public sealed record SnippetJudgement(
    int? BestIndex,
    IReadOnlyList<double>? Scores,
    double? Coverage,
    int TokensUsed,
    string Source,
    IReadOnlyList<double>? InjectionScores = null);

// Relevant is the pass/fail verdict the vision verifier acts on. Score is for score-producing judges only.
// U18 (R26): InjectionScore is the 0..1 indirect-injection score of the findings. Null when the judge did not screen them.
public sealed record VisionJudgement(bool Relevant, double? Score, int TokensUsed, string Source, double? InjectionScore = null);

// U18 (R26 / KTD 10): the indirect-injection screen shared by the KB and Code verifiers. Indexes are 0-based positions in the
// list the judge was given. Only indexes and counts are ever logged, never text.
internal static class IndirectInjectionScreen
{
    // Positions whose score reaches the threshold, in ascending order. Null scores mean the judge did not screen, so nothing is flagged.
    public static IReadOnlyList<int> FlaggedIndexes(IReadOnlyList<double>? injectionScores, int snippetCount, double threshold)
    {
        var flagged = new List<int>();
        if (injectionScores is null)
        {
            return flagged;
        }

        for (var i = 0; i < Math.Min(injectionScores.Count, snippetCount); i++)
        {
            if (injectionScores[i] >= threshold)
            {
                flagged.Add(i);
            }
        }

        return flagged;
    }

    // Removes the flagged positions. They are removed from the end, so the earlier positions stay valid.
    public static void Remove(List<string> snippets, IReadOnlyList<int> flagged)
    {
        for (var i = flagged.Count - 1; i >= 0; i--)
        {
            snippets.RemoveAt(flagged[i]);
        }
    }

    // Keeps Scores aligned with the snippets that remain. Scores beyond the snippet count are kept in place, as ApplyScores ignores them.
    public static IReadOnlyList<double>? RemoveScores(IReadOnlyList<double>? scores, IReadOnlyList<int> flagged) =>
        scores?.Where((_, i) => !flagged.Contains(i)).ToList();

    // Maps a judge's single pick to its position after the flagged snippets are removed. A flagged pick is no pick.
    public static int? RemapIndex(int? index, IReadOnlyList<int> flagged)
    {
        if (index is not int i || flagged.Contains(i))
        {
            return null;
        }

        return i - flagged.Count(removed => removed < i);
    }

    public static string Join(IReadOnlyList<int> indexes) => string.Join(",", indexes);
}

// Grounded is the verdict DrafterAgent acts on. UnsupportedSentences is for judges that can point at specific
// sentences; the LLM judge leaves it null.
public sealed record GroundednessJudgement(bool Grounded, IReadOnlyList<int>? UnsupportedSentences, int TokensUsed, string Source);

public interface IFindingsJudge
{
    // False for the LLM judge: VisionAnalyzerVerifier keeps today's rule of only judging findings shorter than
    // 20 characters (longer findings auto-pass). A judge that returns true is asked about every non-empty finding.
    bool JudgesAllVisionFindings { get; }

    Task<SnippetJudgement> JudgeSnippetsAsync(AgentContext context, FindingsKind kind, IReadOnlyList<string> snippets, CancellationToken ct);
    Task<VisionJudgement> JudgeVisionAsync(AgentContext context, CancellationToken ct);
    Task<GroundednessJudgement> JudgeGroundednessAsync(AgentContext context, string draft, CancellationToken ct);
}
