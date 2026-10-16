using SupportForge.Agents.Jev;
using SupportForge.Agents.Triage;

namespace SupportForge.Agents.Guard;

/// <summary>
/// The combined guard and triage request (U17, KTD 13). Used only when Guard:CombineWithTriage is true, which Program.cs allows only
/// with Triage:Classifier = Jev. One Jev request carries the guard's six Nouls, the triage Choice and the triage Nouls.
/// The guard answer is read strictly, as in JevInputGuard, so a bad guard answer fails open through GuardService. The triage answer
/// is optional: when it is missing or malformed, the assessment carries a null PrecomputedIntent and TriageAgent runs its own path.
/// The customer text still travels only as state values, and the question wording is fixed in code (KTD 14).
/// </summary>
public sealed class TurnAssessor : IInputGuard
{
    private readonly IJevClient _jev;
    private readonly GuardOptions _guardOptions;
    private readonly TriageOptions _triageOptions;

    public TurnAssessor(IJevClient jev, GuardOptions guardOptions, TriageOptions triageOptions)
    {
        _jev = jev ?? throw new ArgumentNullException(nameof(jev));
        _guardOptions = guardOptions ?? throw new ArgumentNullException(nameof(guardOptions));
        _triageOptions = triageOptions ?? throw new ArgumentNullException(nameof(triageOptions));
    }

    public async Task<GuardAssessment> AssessAsync(GuardInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The state is the guard's state: the screened message and the Guard:HistoryTurns window that GuardService already applied.
        // The budget is the larger of the two MaxStateChars, so the triage part never sees a shorter query than it would alone.
        // ProductVersion is included, as triage's own state includes it (R3).
        var state = JevInputGuard.BuildState(
            input,
            _guardOptions.HistoryTurns,
            Math.Max(_guardOptions.MaxStateChars, _triageOptions.MaxStateChars),
            includeProductVersion: true);

        // Built fresh per call. The guard and triage question names don't collide (asserted in TurnAssessorTests).
        var result = await _jev.EvaluateAsync(state, BuildQuestions(), ct);

        // Strict on purpose: a missing or malformed guard answer throws, and GuardService fails open, as for JevInputGuard.
        var signals = JevInputGuard.ReadSignals(result);

        return new GuardAssessment(signals, "jev", ReadPrecomputedIntent(result));
    }

    // Guard and triage questions in one set. Dictionary.Add throws on a duplicate name, so a collision cannot silently
    // replace one question with another.
    internal static IReadOnlyDictionary<string, JevQuestion> BuildQuestions()
    {
        var questions = new Dictionary<string, JevQuestion>(StringComparer.Ordinal);
        foreach (var (name, question) in JevInputGuard.BuildQuestions())
        {
            questions.Add(name, question);
        }

        foreach (var (name, question) in JevIntentClassifier.BuildQuestions())
        {
            questions.Add(name, question);
        }

        return questions;
    }

    // The triage part is optional. A malformed answer returns null rather than throwing, so it never fails the guard (U17).
    // Only the exception types ReadDecision can raise for a bad answer are caught.
    private static IntentDecision? ReadPrecomputedIntent(JevResult result)
    {
        try
        {
            return JevIntentClassifier.ReadDecision(result);
        }
        catch (Exception ex) when (ex is JevResponseException or InvalidOperationException)
        {
            return null;
        }
    }
}
