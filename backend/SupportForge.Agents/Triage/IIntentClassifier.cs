namespace SupportForge.Agents.Triage;

// U2 (JEV intent classification plan): the seam TriageAgent classifies through. Classifiers return label scores
// rather than a bare label so the LLM classifier and a confidence-reporting one (Jev, later) share one shape.
public sealed record IntentDecision(
    IReadOnlyDictionary<string, double> Scores,   // label -> probability. LLM: { chosenLabel: 1.0 }
    double Confidence,                            // LLM: 1.0
    IReadOnlyDictionary<string, double> Signals,  // auxiliary yes/no signals. LLM: empty
    int TokensUsed,
    string Source);                               // "llm" (later also "jev")

public interface IIntentClassifier
{
    // `labels` is the candidate set (TriageLabels.All). Implementations that cannot use it may ignore it.
    Task<IntentDecision> ClassifyAsync(AgentContext context, IReadOnlyList<string> labels, CancellationToken ct);
}
