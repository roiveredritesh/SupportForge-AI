using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Verification;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U11: the verifiers and the Drafter now take an IFindingsJudge. These tests drive them with a mocked judge to
// show they act on the judgement alone (verdict, promotion, source, tokens, retry), with no LLM involved.
public class FindingsJudgeSeamTests
{
    private const string CleanDraft = "This is expected: empty carts are rejected before payment, so nothing was charged.";

    [Fact]
    public async Task KbVerifier_JudgePicksSnippet_PassesPromotesItAndCopiesSourceAndTokens()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), FindingsKind.Kb, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnippetJudgement(BestIndex: 2, Scores: null, Coverage: null, TokensUsed: 77, Source: "jev"));
        var verifier = new KbResearcherVerifier(judge.Object, new ListLogger<KbResearcherVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.Add("first");
        context.KbSnippets.Add("second");
        context.KbSnippets.Add("the answer");

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        Assert.Equal("the answer", context.KbSnippets[0]);
        Assert.Equal("jev", result.KbVerification.Source);
        Assert.Equal(77, context.TokensByAgent["KbResearcherVerifier"]);
    }

    [Fact]
    public async Task CodeVerifier_JudgeNoMatch_FirstAttempt_RetriesAndKeepsSnippets()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), FindingsKind.Code, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnippetJudgement(BestIndex: null, Scores: null, Coverage: null, TokensUsed: 5, Source: "llm"));
        var verifier = new CodeAnalyzerVerifier(judge.Object, new ListLogger<CodeAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.Add("NODE one");
        context.CodeSnippets.Add("NODE two");
        context.CodeVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.CodeVerification.Status);
        Assert.Equal(2, context.CodeSnippets.Count);
        Assert.Equal("llm", result.CodeVerification.Source);
    }

    [Fact]
    public async Task CodeVerifier_JudgeNoMatch_SecondAttempt_FailsFinalAndClearsSnippets()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), FindingsKind.Code, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnippetJudgement(BestIndex: null, Scores: null, Coverage: null, TokensUsed: 5, Source: "llm"));
        var verifier = new CodeAnalyzerVerifier(judge.Object, new ListLogger<CodeAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.Add("NODE one");
        context.CodeVerification.Attempts = 2; // at limit, so a judged "none" is final

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.CodeVerification.Status);
        Assert.Empty(context.CodeSnippets);
    }

    [Fact]
    public async Task VisionVerifier_JudgesAllFindings_LongFindingJudgedNotRelevant_FailsRetrying()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.SetupGet(j => j.JudgesAllVisionFindings).Returns(true);
        judge.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionJudgement(Relevant: false, Score: null, TokensUsed: 3, Source: "jev"));
        var verifier = new VisionAnalyzerVerifier(judge.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", Intent = "screenshot_error", ScreenshotBase64 = "data",
            VisionFindings = new string('x', 200), // well past the 20-character gate
        };
        context.VisionVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.VisionVerification.Status);
        Assert.Equal("jev", result.VisionVerification.Source);
        Assert.Equal(3, context.TokensByAgent["VisionAnalyzerVerifier"]);
        judge.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VisionVerifier_JudgesAllFindings_LongFindingJudgedRelevant_Passes()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.SetupGet(j => j.JudgesAllVisionFindings).Returns(true);
        judge.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionJudgement(Relevant: true, Score: null, TokensUsed: 3, Source: "jev"));
        var verifier = new VisionAnalyzerVerifier(judge.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", Intent = "screenshot_error", ScreenshotBase64 = "data",
            VisionFindings = new string('x', 200),
        };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.VisionVerification.Status);
        judge.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VisionVerifier_DefaultGate_LongFindingPassesWithoutCallingJudge()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.SetupGet(j => j.JudgesAllVisionFindings).Returns(false);
        var verifier = new VisionAnalyzerVerifier(judge.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", Intent = "screenshot_error", ScreenshotBase64 = "data",
            VisionFindings = new string('x', 200),
        };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.VisionVerification.Status);
        judge.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DrafterGroundedness_JudgeUngrounded_HalvesConfidenceRelativeToGroundedRun()
    {
        var (groundedContext, _) = await RunDrafterAsync(isGrounded: true, groundednessCheckEnabled: true);
        var (ungroundedContext, _) = await RunDrafterAsync(isGrounded: false, groundednessCheckEnabled: true);

        // Flagged, not blocked: the draft is kept and only confidence is halved.
        Assert.Equal(CleanDraft, ungroundedContext.Draft);
        Assert.Equal(groundedContext.Confidence * 0.5, ungroundedContext.Confidence);
        Assert.Equal(DrafterAgent.ComputeConfidence(groundedContext), groundedContext.Confidence);
    }

    [Fact]
    public async Task DrafterGroundedness_Disabled_JudgeNeverCalled()
    {
        var (context, judge) = await RunDrafterAsync(isGrounded: false, groundednessCheckEnabled: false);

        judge.Verify(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DrafterAgent.ComputeConfidence(context), context.Confidence);
    }

    [Fact]
    public async Task DrafterGroundedness_JudgeGetsTheDraftAndTokensLandUnderGroundednessKey()
    {
        var (context, judge) = await RunDrafterAsync(isGrounded: true, groundednessCheckEnabled: true);

        judge.Verify(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), CleanDraft, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(9, context.TokensByAgent["Drafter.Groundedness"]);
    }

    // Runs the Drafter once with a mocked draft LLM and a mocked groundedness judge. Returns the context and judge.
    private static async Task<(AgentContext Context, Mock<IFindingsJudge> Judge)> RunDrafterAsync(bool isGrounded, bool groundednessCheckEnabled)
    {
        var context = new AgentContext { ProjectId = "p", Query = "why does checkout fail" };
        context.KbVerification.Status = VerificationStatus.Passed;
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(CleanDraft);
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GroundednessJudgement(Grounded: isGrounded, UnsupportedSentences: null, TokensUsed: 9, Source: "jev"));

        await new DrafterAgent(llm.Object, new ListLogger<DrafterAgent>(), groundednessCheckEnabled, judge.Object).RunAsync(context);

        return (context, judge);
    }
}
