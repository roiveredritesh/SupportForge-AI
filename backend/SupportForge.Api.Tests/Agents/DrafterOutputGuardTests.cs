using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Guard;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U19 (R27): DrafterAgent applies the output guard after LooksLikeLeak. The guard is optional, so existing
// DrafterAgent call sites keep their behaviour.
public class DrafterOutputGuardTests
{
    private const string CleanDraft = "This is expected: empty carts are rejected before payment, so nothing was charged.";

    private static AgentContext PassedContext()
    {
        var context = new AgentContext { ProjectId = "p", Query = "why does checkout fail" };
        context.KbVerification.Status = VerificationStatus.Passed;
        return context;
    }

    private static Mock<ILlmClient> LlmReturning(string reply)
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(reply);
        return llm;
    }

    private static Mock<IOutputGuard> GuardReturning(double disclosure)
    {
        var guard = new Mock<IOutputGuard>();
        guard.Setup(g => g.AssessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Dictionary<string, double>
             {
                 [OutputGuardSignals.Disclosure] = disclosure,
                 [OutputGuardSignals.FollowsInjected] = 0.0,
             });
        return guard;
    }

    private static OutputGuardService Service(GuardMode mode, Mock<IOutputGuard> guard) =>
        new(new GuardOptions { Mode = mode }, guard.Object, new ListLogger<OutputGuardService>());

    [Fact]
    public async Task Enforce_Fired_ReplacesDraftWithLeakFallback_ZeroConfidence_AndSkipsGroundedness()
    {
        var llm = LlmReturning(CleanDraft);
        var context = PassedContext();

        await new DrafterAgent(llm.Object, new ListLogger<DrafterAgent>(), groundednessCheckEnabled: true,
                outputGuard: Service(GuardMode.Enforce, GuardReturning(0.95)))
            .RunAsync(context);

        Assert.Equal(DrafterAgent.LeakFallback, context.Draft);
        Assert.Equal(0.0, context.Confidence);
        // Draft only. A groundedness judge call would make this two.
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shadow_Fired_KeepsDraft_AndConfidenceUnchanged()
    {
        var llm = LlmReturning(CleanDraft);
        var context = PassedContext();
        var expectedConfidence = DrafterAgent.ComputeConfidence(context);

        await new DrafterAgent(llm.Object, new ListLogger<DrafterAgent>(),
                outputGuard: Service(GuardMode.Shadow, GuardReturning(0.95)))
            .RunAsync(context);

        Assert.Equal(CleanDraft, context.Draft);
        Assert.Equal(expectedConfidence, context.Confidence);
    }

    [Fact]
    public async Task Enforce_NotFired_KeepsDraft_AndConfidenceUnchanged()
    {
        var llm = LlmReturning(CleanDraft);
        var context = PassedContext();
        var expectedConfidence = DrafterAgent.ComputeConfidence(context);

        await new DrafterAgent(llm.Object, new ListLogger<DrafterAgent>(),
                outputGuard: Service(GuardMode.Enforce, GuardReturning(0.1)))
            .RunAsync(context);

        Assert.Equal(CleanDraft, context.Draft);
        Assert.Equal(expectedConfidence, context.Confidence);
    }

    [Fact]
    public async Task NoGuard_DraftAndConfidenceUnchanged()
    {
        var llm = LlmReturning(CleanDraft);
        var context = PassedContext();
        var expectedConfidence = DrafterAgent.ComputeConfidence(context);

        await new DrafterAgent(llm.Object, new ListLogger<DrafterAgent>()).RunAsync(context);

        Assert.Equal(CleanDraft, context.Draft);
        Assert.Equal(expectedConfidence, context.Confidence);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeakedDraft_IsNotScreenedAgain_ByTheOutputGuard()
    {
        // Both the draft and the leak retry contain a source file reference, so LooksLikeLeak fires and the output guard must not run.
        var llm = LlmReturning("The fix is in OrderService.cs:42.");
        var guard = GuardReturning(0.95);
        var context = PassedContext();

        await new DrafterAgent(llm.Object, new ListLogger<DrafterAgent>(), outputGuard: Service(GuardMode.Enforce, guard))
            .RunAsync(context);

        Assert.Equal(DrafterAgent.LeakFallback, context.Draft);
        Assert.Equal(0.0, context.Confidence);
        guard.Verify(g => g.AssessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
