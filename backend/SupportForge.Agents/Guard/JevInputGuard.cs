using SupportForge.Agents.Jev;

namespace SupportForge.Agents.Guard;

/// <summary>
/// Scores a customer turn for the six <see cref="GuardSignals"/> with one Jev request of atomic Noul
/// questions (U16). The customer text travels only as a value in the state object. Question wording
/// is the fixed <see cref="GuardSignal.Statement"/> and never contains user text (KTD 14), so an
/// injection attempt can change a score at worst, never the instructions Jev is asked to follow.
/// </summary>
public sealed class JevInputGuard : IInputGuard
{
    private readonly IJevClient _jev;
    private readonly GuardOptions _options;

    public JevInputGuard(IJevClient jev, GuardOptions options)
    {
        _jev = jev ?? throw new ArgumentNullException(nameof(jev));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<GuardAssessment> AssessAsync(GuardInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var state = BuildState(input);

        // Built fresh per call: the dictionary handed to the client is never shared mutable state.
        var questions = new Dictionary<string, JevQuestion>(StringComparer.Ordinal);
        foreach (var signal in GuardSignals.All)
        {
            questions[signal.Name] = new JevNoulQuestion(signal.Statement);
        }

        // Exceptions propagate on purpose. GuardService owns the timeout and turns any failure into the
        // fail-open (or FailClosed) path, with the logging that goes with it.
        var result = await _jev.EvaluateAsync(state, questions, ct);

        var signals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var signal in GuardSignals.All)
        {
            // Result.Noul throws JevResponseException when an answer is missing or has the wrong type,
            // which is the same failure path as a transport error.
            signals[signal.Name] = result.Noul(signal.Name).Noul;
        }

        return new GuardAssessment(signals, "jev");
    }

    private object BuildState(GuardInput input)
    {
        var normalized = InputNormalizer.Normalize(input.Message);

        // Only the newest HistoryTurns entries are screened, as in HeuristicInputGuard. Assistant turns
        // stay in the state as context but their decoded segments are excluded: that text is our own output.
        var keep = Math.Max(0, _options.HistoryTurns);
        var history = (input.History ?? Array.Empty<(string Role, string Content)>())
            .TakeLast(keep)
            .Select(turn => new ScreenedTurn(turn.Role, InputNormalizer.Normalize(turn.Content)))
            .ToList();

        // MaxStateChars budget over message and history text. Oldest history goes first, because the
        // current message and the newest context matter most. Only when the message alone exceeds the
        // budget is the message itself cut, and then it keeps its END: attack text tends to sit in the
        // latest message, so a head-first cut would lose exactly what the guard needs to see.
        var budget = Math.Max(0, _options.MaxStateChars);
        var text = normalized.Text;
        while (history.Count > 0 && text.Length + history.Sum(t => t.Content.Text.Length) > budget)
        {
            history.RemoveAt(0);
        }

        if (text.Length > budget)
        {
            text = KeepTail(text, budget);
        }

        var decodedSegments = new List<string>(normalized.DecodedSegments);
        foreach (var turn in history)
        {
            if (string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                decodedSegments.AddRange(turn.Content.DecodedSegments);
            }
        }

        // Decoded segments sit outside the MaxStateChars text budget, so their total is capped separately (message
        // segments first): InputNormalizer allows 5 per text, and 5 texts at 2000 characters each would otherwise
        // let one obfuscated conversation add ~50k characters to every guard request.
        const int MaxDecodedSegments = 5;
        if (decodedSegments.Count > MaxDecodedSegments)
        {
            decodedSegments.RemoveRange(MaxDecodedSegments, decodedSegments.Count - MaxDecodedSegments);
        }

        return new
        {
            message = text,
            decodedSegments,
            history = history.Select(t => new { role = t.Role, content = t.Content.Text }).ToList(),
            hasScreenshot = input.HasScreenshot,
        };
    }

    private static string KeepTail(string text, int max)
    {
        if (text.Length <= max) return text;

        var start = text.Length - max;

        // Do not open on the low half of a surrogate pair. A lone surrogate is not valid text and would
        // make the serialized state mangled.
        if (start < text.Length && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..];
    }

    private sealed record ScreenedTurn(string Role, NormalizedInput Content);
}
