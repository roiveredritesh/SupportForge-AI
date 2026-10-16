namespace SupportForge.Agents.Guard;

public interface IInputGuard
{
    Task<GuardAssessment> AssessAsync(GuardInput input, CancellationToken ct);
}
