namespace SupportForge.Agents.Guard;

/// <summary>
/// Turns guard scores into a verdict. Two rules carry most of the weight: a heuristic hit can only
/// flag, and a block needs a Jev score at or above threshold on a block-class signal.
/// </summary>
public static class GuardPolicy
{
    // Heuristic scores are exactly 0 or 1. Jev scores are continuous. Anything at or above this counts
    // as "the heuristic matched" without depending on either scale.
    private const double HeuristicHit = 0.5;

    public static GuardDecision Decide(GuardOptions options, GuardAssessment heuristic, GuardAssessment? jev, bool jevFailed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(heuristic);

        if (options.Mode == GuardMode.Off)
        {
            return new GuardDecision(GuardVerdict.Allowed, new Dictionary<string, double>(), [], "off");
        }

        var heuristicScores = Complete(heuristic.Signals);
        var heuristicHits = GuardSignals.All
            .Where(s => heuristicScores[s.Name] >= HeuristicHit)
            .Select(s => s.Name)
            .ToArray();

        // Fail-open is the product default. Only an explicit FailClosed in Enforce refuses the turn.
        if (jevFailed)
        {
            if (options.Mode == GuardMode.Enforce && options.FailClosed)
            {
                return new GuardDecision(GuardVerdict.Blocked, heuristicScores, [], "jev-failed");
            }

            return new GuardDecision(GuardVerdict.Unscreened, heuristicScores, heuristicHits, "jev-failed");
        }

        if (jev is null)
        {
            var heuristicVerdict = heuristicHits.Length > 0 ? GuardVerdict.Flagged : GuardVerdict.Allowed;
            return new GuardDecision(heuristicVerdict, heuristicScores, heuristicHits, "heuristic");
        }

        var jevScores = Complete(jev.Signals);
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        var fired = new HashSet<string>(StringComparer.Ordinal);
        var jevBlocks = false;

        foreach (var signal in GuardSignals.All)
        {
            var jevScore = jevScores[signal.Name];
            var heuristicScore = heuristicScores[signal.Name];
            merged[signal.Name] = Math.Max(jevScore, heuristicScore);

            if (jevScore >= options.ThresholdFor(signal))
            {
                fired.Add(signal.Name);
                if (signal.Class == GuardSignalClass.Block) jevBlocks = true;
            }

            if (heuristicScore >= HeuristicHit) fired.Add(signal.Name);
        }

        GuardVerdict verdict;
        if (jevBlocks)
        {
            verdict = options.Mode == GuardMode.Enforce ? GuardVerdict.Blocked : GuardVerdict.Flagged;
        }
        else
        {
            verdict = fired.Count > 0 ? GuardVerdict.Flagged : GuardVerdict.Allowed;
        }

        var firedSorted = fired.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return new GuardDecision(verdict, merged, firedSorted, "jev");
    }

    // A guard that omits a signal means "no evidence", so missing keys read as 0. Only the six known
    // signals are kept, which keeps the decision's shape the same whatever a guard returns.
    private static Dictionary<string, double> Complete(IReadOnlyDictionary<string, double> scores)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var signal in GuardSignals.All)
        {
            result[signal.Name] = scores.TryGetValue(signal.Name, out var value) ? value : 0.0;
        }
        return result;
    }
}
