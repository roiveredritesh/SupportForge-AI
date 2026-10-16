namespace SupportForge.Agents;

public enum VerificationStatus { NotRun, Passed, FailedRetrying, FailedFinal }

public sealed class VerificationResult
{
    public VerificationStatus Status { get; set; } = VerificationStatus.NotRun;
    public int Attempts { get; set; }
    public string? Reason { get; set; }

    // U11 (JEV intent classification plan): what the judge reported beyond the verdict. Scores and Coverage
    // are filled only by a judge that produces them (the LLM judge leaves them null). Source names the judge
    // that decided ("llm", later "jev"); it stays empty when no judge ran.
    public IReadOnlyList<double>? Scores { get; set; }
    public double? Coverage { get; set; }
    public string Source { get; set; } = "";
}
