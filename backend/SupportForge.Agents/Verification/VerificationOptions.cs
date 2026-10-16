namespace SupportForge.Agents.Verification;

// U12 (JEV intent classification plan, R20): which findings judge acts. Llm is the default and the fallback for Jev.
public enum FindingsJudgeMode { Llm, Jev, Shadow }

// R21: Jev options for the findings judge. Code-graph snippets (customer identifiers, file paths) go to Jev only when IncludeCode is true.
public sealed class VerificationJevOptions
{
    public bool IncludeCode { get; set; }
}

// Config section "Verification". The defaults are placeholders until the U10 eval recommends values.
public sealed class VerificationOptions
{
    public FindingsJudgeMode Judge { get; set; } = FindingsJudgeMode.Llm;

    // Placeholder until the U10 eval recommends a value. A Jev score at or above this keeps a finding (R15).
    public double MinRelevance { get; set; } = 0.5;

    // U13: coverage below this sets VerificationResult.CoverageIsPartial, which adds a hint to the Drafter prompt.
    public double PartialCoverageBelow { get; set; } = 0.67;

    // U12: each snippet is cut to this many characters before it goes into the Jev state, keeping its start.
    public int MaxSnippetChars { get; set; } = 2000;

    // U12: the whole Jev state for a findings judgement stays under this many characters (same budget as the triage state).
    public int MaxStateChars { get; set; } = 16000;

    public int HistoryTurns { get; set; } = 4;

    // U12: Jev runs under this budget. Past it, the LLM judge answers in the same call (R20).
    public TimeSpan JevTimeout { get; set; } = TimeSpan.FromSeconds(2);

    // U13 / R18: sentence-level groundedness. Off by default. Jev judges sentences only when this is on.
    public bool GroundednessPerSentence { get; set; }

    public int MaxSentences { get; set; } = 20;

    // U18 (R26): a retrieved snippet or vision finding whose indirect-injection score reaches this is dropped, whatever its relevance.
    // Filled from Guard:IndirectThreshold by the composition root.
    public double IndirectThreshold { get; set; } = 0.8;

    public VerificationJevOptions Jev { get; set; } = new();
}
