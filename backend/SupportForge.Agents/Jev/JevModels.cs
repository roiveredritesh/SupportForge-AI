namespace SupportForge.Agents.Jev;

/// <summary>
/// One question asked of Jev about the shared state. Each question is evaluated independently against
/// the same state, so keep each one atomic and compose the results in code (docs/integrations/typesafe-jev-api.md).
/// </summary>
public abstract record JevQuestion(string? Instructions);

/// <summary>Picks exactly one label. <paramref name="Criteria"/> maps label to description; a null description is sent as JSON null.</summary>
public sealed record JevChoiceQuestion(string? Instructions, IReadOnlyDictionary<string, string?> Criteria) : JevQuestion(Instructions);

/// <summary>
/// Yes/no (true/false) question. Needs <paramref name="Instructions"/> or at least one described side
/// (<paramref name="WhenTrue"/> / <paramref name="WhenFalse"/>); the contract rejects anything else.
/// </summary>
public sealed record JevNoulQuestion(string? Instructions, string? WhenTrue = null, string? WhenFalse = null) : JevQuestion(Instructions);

/// <summary>Rubric rating. Position in <paramref name="Criteria"/> is the score level, counted from 0.</summary>
public sealed record JevScoreQuestion(string? Instructions, IReadOnlyList<string> Criteria) : JevQuestion(Instructions);

/// <summary>A typed answer to one asked question. The concrete type matches the question's type.</summary>
public abstract record JevAnswer;

public sealed record JevChoiceAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : JevAnswer;

/// <summary>Probability of yes/true, 0 to 1. Values near 0.5 mean uncertain.</summary>
public sealed record JevNoulAnswer(double Noul) : JevAnswer;

/// <summary><paramref name="Score"/> is the probability-weighted average of the levels, so it can fall between them.</summary>
public sealed record JevScoreAnswer(double Score, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : JevAnswer;

/// <summary>
/// The answers to one Jev request. <see cref="Answers"/> holds only the questions that were asked, keyed by
/// the names the caller chose. Use the typed accessors, which throw <see cref="JevResponseException"/> when
/// the name is missing or the answer has a different type.
/// </summary>
public sealed record JevResult(string Model, IReadOnlyDictionary<string, JevAnswer> Answers, int InputTokens, int OutputTokens, string? RequestId)
{
    // Input tokens are billable; output tokens are currently free (contract, Response section).
    public int TotalTokens => InputTokens + OutputTokens;

    public JevChoiceAnswer Choice(string name) => Typed<JevChoiceAnswer>(name, "choice");

    public JevNoulAnswer Noul(string name) => Typed<JevNoulAnswer>(name, "noul");

    public JevScoreAnswer Score(string name) => Typed<JevScoreAnswer>(name, "score");

    private T Typed<T>(string name, string expected) where T : JevAnswer
    {
        // The accessors are how callers read answers, so a missing or mistyped answer must fail loudly here
        // rather than return a default that a guard could mistake for a real score (U4).
        if (!Answers.TryGetValue(name, out var answer))
            throw new JevResponseException($"Jev returned no answer for question '{name}'.", RequestId);
        if (answer is T typed)
            return typed;
        throw new JevResponseException($"Jev answer '{name}' is not a {expected} answer.", RequestId);
    }
}
