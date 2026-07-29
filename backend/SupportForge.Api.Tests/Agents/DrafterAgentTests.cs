using Moq;
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

    [Fact]
    public void BuildUserPrompt_WithNoHistory_ShowsNone()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error" };

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Contains("Conversation so far: (none)", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithHistory_IncludesRecap()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "What about the other file?" };
        context.History.Add(("user", "Getting a 500 error on checkout"));
        context.History.Add(("assistant", "The 500 comes from OrderService.cs:42"));

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Contains("user: Getting a 500 error on checkout", prompt);
        Assert.Contains("assistant: The 500 comes from OrderService.cs:42", prompt);
    }

    [Fact]
    public void SystemPrompt_NoLongerInstructsVerbatimQuotingOrFileCitation()
    {
        Assert.DoesNotContain("quote code verbatim", DrafterAgent.SystemPrompt);
        Assert.DoesNotContain("cite the file", DrafterAgent.SystemPrompt);
    }

    [Fact]
    public void SystemPrompt_ForbidsExposingCodeKbTextAndCitations()
    {
        Assert.Contains("Never show source code", DrafterAgent.SystemPrompt);
        Assert.Contains("closely paraphrase any code", DrafterAgent.SystemPrompt);
        Assert.Contains("Never quote or closely paraphrase KB document text", DrafterAgent.SystemPrompt);
        Assert.Contains("Never mention a file name, path, line number", DrafterAgent.SystemPrompt);
        Assert.Contains("citation or evidence", DrafterAgent.SystemPrompt);
    }

    [Fact]
    public void SystemPrompt_DescribesThreeWayCodeIssueClassification()
    {
        Assert.Contains("code_issue", DrafterAgent.SystemPrompt);
        Assert.Contains("Working as expected", DrafterAgent.SystemPrompt);
        Assert.Contains("Advisory data or configuration fix", DrafterAgent.SystemPrompt);
        Assert.Contains("Needs a code change", DrafterAgent.SystemPrompt);
        Assert.Contains("engineering team", DrafterAgent.SystemPrompt);
    }

    [Fact]
    public void SystemPrompt_AdvisoryOutcomeIsNotFramedAsLiveDiagnosis()
    {
        Assert.Contains("never as a diagnosis", DrafterAgent.SystemPrompt);
        Assert.Contains("no access to their live environment", DrafterAgent.SystemPrompt);
    }

    [Fact]
    public void SystemPrompt_AsksOneClarifyingQuestionWhenIntentIsUnclear()
    {
        Assert.Contains("\"unclear\"", DrafterAgent.SystemPrompt);
        Assert.Contains("ask exactly one clarifying question and nothing else", DrafterAgent.SystemPrompt);
    }

    [Fact]
    public void LooksLikeLeak_DraftWithCodeFence_IsFlagged()
    {
        var draft = "Here's the handler:\n```csharp\nvoid Checkout() { }\n```";

        Assert.True(DrafterAgent.LooksLikeLeak(draft, []));
    }

    [Fact]
    public void LooksLikeLeak_DraftWithSourceFilePath_IsFlagged()
    {
        Assert.True(DrafterAgent.LooksLikeLeak("The bug is in OrderService.cs around checkout.", []));
        Assert.True(DrafterAgent.LooksLikeLeak("See src/api/handlers/checkout.ts line 42.", []));
    }

    [Fact]
    public void LooksLikeLeak_DraftCopyingSnippetVerbatim_IsFlagged()
    {
        var snippet = "if (order.Total <= 0) throw new InvalidOperationException(\"empty cart\");";

        var draft = $"The rule works like this: {snippet} and that is why it fails.";

        Assert.True(DrafterAgent.LooksLikeLeak(draft, [snippet]));
    }

    [Fact]
    public void LooksLikeLeak_CleanProse_IsNotFlagged()
    {
        var draft = "This is expected behaviour: orders with an empty cart are rejected before payment, "
                    + "so no charge is made. Nothing needs to be fixed on your side.";

        Assert.False(DrafterAgent.LooksLikeLeak(draft, ["if (order.Total <= 0) throw new InvalidOperationException(\"empty cart\");"]));
    }

    [Fact]
    public async Task RunAsync_LeakingDraft_IsReplacedWithFallback()
    {
        var context = new AgentContext { ProjectId = "p", Query = "why does checkout fail" };
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The fix is in OrderService.cs:42.");

        await new DrafterAgent(llm.Object).RunAsync(context);

        Assert.Equal(DrafterAgent.LeakFallback, context.Draft);
    }

    [Fact]
    public void BuildUserPrompt_CarriesFileCitedCodeSnippets_FromCodeAnalyzer()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail" };
        context.CodeSnippets.Add("// OrderService.cs\npublic void Checkout() { throw new Exception(); }");

        var prompt = DrafterAgent.BuildUserPrompt(context);

        Assert.Contains("// OrderService.cs", prompt);
    }
}
