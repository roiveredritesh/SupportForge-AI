namespace SupportForge.Agents;

public enum VerificationStatus { NotRun, Passed, FailedRetrying, FailedFinal }

public sealed class VerificationResult
{
    public VerificationStatus Status { get; set; } = VerificationStatus.NotRun;
    public int Attempts { get; set; }
    public string? Reason { get; set; }
}
