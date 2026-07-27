using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class VerificationResultTests
{
    [Fact]
    public void AgentContext_StartsWithNotRunVerificationForAllThreeBranches()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "test" };

        Assert.Equal(VerificationStatus.NotRun, context.KbVerification.Status);
        Assert.Equal(VerificationStatus.NotRun, context.CodeVerification.Status);
        Assert.Equal(VerificationStatus.NotRun, context.VisionVerification.Status);
        Assert.Equal(0, context.KbVerification.Attempts);
    }
}
