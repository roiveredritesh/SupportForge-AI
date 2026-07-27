using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class DrafterAgentTests
{
    [Fact]
    public void ComputeConfidence_AllApplicableBranchesPassed_ReturnsHigh()
    {
        var context = new AgentContext { ProjectId = "p", Query = "q" };
        context.KbVerification.Status = VerificationStatus.Passed;
        context.CodeVerification.Status = VerificationStatus.NotRun; // not applicable to this query
        context.VisionVerification.Status = VerificationStatus.NotRun;

        var confidence = DrafterAgent.ComputeConfidence(context);

        Assert.True(confidence >= 0.7, $"expected High-bucket confidence, got {confidence}");
    }

    [Fact]
    public void ComputeConfidence_NoBranchesApplicable_ReturnsLow()
    {
        var context = new AgentContext { ProjectId = "p", Query = "q" };
        // all three stay NotRun

        var confidence = DrafterAgent.ComputeConfidence(context);

        Assert.True(confidence < 0.4, $"expected Low-bucket confidence, got {confidence}");
    }

    [Fact]
    public void ComputeConfidence_MixOfPassedAndFailedFinal_ReturnsMedium()
    {
        var context = new AgentContext { ProjectId = "p", Query = "q" };
        context.KbVerification.Status = VerificationStatus.Passed;
        context.CodeVerification.Status = VerificationStatus.FailedFinal;
        context.VisionVerification.Status = VerificationStatus.NotRun;

        var confidence = DrafterAgent.ComputeConfidence(context);

        Assert.InRange(confidence, 0.4, 0.69);
    }

    [Fact]
    public void ComputeConfidence_AllApplicableBranchesFailedFinal_ReturnsLow()
    {
        var context = new AgentContext { ProjectId = "p", Query = "q" };
        context.KbVerification.Status = VerificationStatus.FailedFinal;
        context.CodeVerification.Status = VerificationStatus.NotRun;
        context.VisionVerification.Status = VerificationStatus.NotRun;

        var confidence = DrafterAgent.ComputeConfidence(context);

        Assert.True(confidence < 0.4, $"expected Low-bucket confidence, got {confidence}");
    }

    [Fact]
    public void BuildUserPrompt_CapsSnippetsAtFivePerSource()
    {
        var context = new AgentContext { ProjectId = "p", Query = "q" };
        for (var i = 0; i < 10; i++) context.KbSnippets.Add($"kb-snippet-{i}");

        var prompt = DrafterAgent.BuildUserPrompt(context);

        for (var i = 0; i < 5; i++) Assert.Contains($"kb-snippet-{i}", prompt);
        for (var i = 5; i < 10; i++) Assert.DoesNotContain($"kb-snippet-{i}", prompt);
    }
}
