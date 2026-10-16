namespace SupportForge.Agents.Verification;

// U11 (JEV intent classification plan, R13 / KTD 9): the one seam every findings judge goes through. The
// KB, Code and Vision verifiers and the Drafter's groundedness check all ask an IFindingsJudge instead of
// calling the LLM directly. LlmFindingsJudge is the behaviour-preserving implementation; the Jev judge
// (U12) implements the same interface without an LLM, so the verifiers never learn which judge decided.
public enum FindingsKind { Kb, Code }

// BestIndex is the 0-based index of the single snippet that answers the question, or null for "none".
// Scores and Coverage are for judges that produce them (the Jev judge); the LLM judge leaves both null.
// Source names the judge that decided ("llm", later "jev").
public sealed record SnippetJudgement(
    int? BestIndex,
    IReadOnlyList<double>? Scores,
    double? Coverage,
    int TokensUsed,
    string Source);

// Relevant is the pass/fail verdict the vision verifier acts on. Score is for score-producing judges only.
public sealed record VisionJudgement(bool Relevant, double? Score, int TokensUsed, string Source);

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
