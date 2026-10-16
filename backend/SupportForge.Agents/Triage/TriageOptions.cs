namespace SupportForge.Agents.Triage;

// U5 (JEV intent classification plan): which intent classifier TriageAgent runs (R7). Llm is the default and the
// only mode that needs no Jev key (R12). Shadow runs both and acts on the LLM result (U6).
public enum TriageClassifierMode { Llm, Jev, Shadow }

// U5: the thresholds and switches read by TriageDecisionPolicy and JevIntentClassifier (R4, R8). The defaults are
// placeholders, not tuned values. U1's eval recommends the real MinConfidence and MinMargin before Jev is enabled.
public sealed class TriageOptions
{
    public TriageClassifierMode Classifier { get; set; } = TriageClassifierMode.Llm;

    // Below this confidence the policy returns "unclear" (R4).
    public double MinConfidence { get; set; } = 0.6;     // placeholder until the U1 eval recommends a value

    // Below this gap between the top label and the best other label the policy returns "unclear" (R4).
    public double MinMargin { get; set; } = 0.15;

    // Above this, the Drafter can answer without confirming its reading (R9). Used by the Drafter hint, not by the policy.
    public double HighConfidence { get; set; } = 0.85;

    // Opt-in: a confident runner-up may also enable its retrieval branch (R8). Off until the eval supports it.
    public bool SecondaryIntentRouting { get; set; } = false;
    public double SecondaryMinProbability { get; set; } = 0.35;

    // Most recent history turns sent to Jev as context (R3).
    public int HistoryTurns { get; set; } = 4;

    // Upper bound on the serialized Jev state (U5 step 1). Oldest history is dropped first, then the query is cut from its start.
    public int MaxStateChars { get; set; } = 8000;

    // Per-request budget for the Jev call (R6). A timeout falls back to the LLM classifier.
    public TimeSpan JevTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
}
