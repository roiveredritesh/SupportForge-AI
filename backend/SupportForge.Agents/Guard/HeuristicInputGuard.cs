namespace SupportForge.Agents.Guard;

/// <summary>
/// Free, synchronous phrase screen. It always runs and only ever contributes flags: GuardPolicy never
/// lets a heuristic hit block a customer on its own.
/// </summary>
public sealed class HeuristicInputGuard : IInputGuard
{
    private readonly GuardOptions _options;

    public HeuristicInputGuard(GuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public Task<GuardAssessment> AssessAsync(GuardInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Each text is scanned on its own. Joining them would let a pattern match across a turn
        // boundary, which is a false positive waiting to happen.
        var texts = new List<string>();
        var decodedCount = 0;

        var message = InputNormalizer.Normalize(input.Message);
        texts.Add(message.Text);
        texts.AddRange(message.DecodedSegments);
        decodedCount += message.DecodedSegments.Count;

        // Only customer turns are scanned. Assistant turns are our own output, and quoting an earlier
        // refusal back ("I can't ignore previous instructions") must not trip the guard.
        var history = input.History ?? Array.Empty<(string Role, string Content)>();
        var userTurns = history
            .Where(h => string.Equals(h.Role, "user", StringComparison.OrdinalIgnoreCase))
            .TakeLast(Math.Max(0, _options.HistoryTurns));
        foreach (var turn in userTurns)
        {
            var normalized = InputNormalizer.Normalize(turn.Content);
            texts.Add(normalized.Text);
            texts.AddRange(normalized.DecodedSegments);
            decodedCount += normalized.DecodedSegments.Count;
        }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var signal in GuardSignals.All)
        {
            scores[signal.Name] = 0.0;
        }

        foreach (var (signal, pattern) in InjectionPhrases.Heuristics)
        {
            if (texts.Any(text => pattern.IsMatch(text)))
            {
                scores[signal] = 1.0;
            }
        }

        scores[GuardSignals.ObfuscatedPayload] = decodedCount > 0 ? 1.0 : 0.0;

        return Task.FromResult(new GuardAssessment(scores, "heuristic"));
    }
}
