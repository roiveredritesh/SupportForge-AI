using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Verification;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U12 (R15, R17): the score-based verifier policy. The verifiers act on a mocked IFindingsJudge here, so no LLM or Jev call is involved.
public class VerifierScorePolicyTests
{
    private static readonly string[] FiveSnippets = ["s0", "s1", "s2", "s3", "s4"];

    // Two of the five scores clear the default MinRelevance (0.5): s1 at 0.9 and s3 at 0.7.
    private static readonly double[] TwoAboveThreshold = [0.2, 0.9, 0.4, 0.7, 0.1];

    private static Mock<IFindingsJudge> ScoredJudge(FindingsKind kind, SnippetJudgement judgement)
    {
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), kind, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(judgement);
        return judge;
    }

    private static SnippetJudgement Scored(IReadOnlyList<double> scores, double? coverage = null) =>
        new(BestIndex: null, Scores: scores, Coverage: coverage, TokensUsed: 4, Source: "jev");

    private static AgentContext KbContext(int attempts = 0)
    {
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.AddRange(FiveSnippets);
        context.KbVerification.Attempts = attempts;
        return context;
    }

    private static AgentContext CodeContext(int attempts = 0)
    {
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.AddRange(FiveSnippets);
        context.CodeVerification.Attempts = attempts;
        return context;
    }

    private static KbResearcherVerifier KbVerifier(IFindingsJudge judge) =>
        new(judge, new VerificationOptions(), new ListLogger<KbResearcherVerifier>());

    private static CodeAnalyzerVerifier CodeVerifier(IFindingsJudge judge) =>
        new(judge, new VerificationOptions(), new ListLogger<CodeAnalyzerVerifier>());

    [Fact]
    public async Task Kb_TwoOfFiveAboveThreshold_KeepsThoseTwoInScoreOrder_DropsTheRest_Passes()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored(TwoAboveThreshold));
        var context = KbContext();

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        Assert.Equal(new[] { "s1", "s3" }, context.KbSnippets);
        judge.Verify(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), FindingsKind.Kb, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Kb_EqualScores_KeepRetrievalOrder()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored([0.8, 0.6, 0.8]));
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.AddRange(["s0", "s1", "s2"]);

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        Assert.Equal(new[] { "s0", "s2", "s1" }, context.KbSnippets);
    }

    [Fact]
    public async Task Kb_ScoresBeyondSnippetCount_AreIgnored()
    {
        // A judge that returns more scores than snippets must not keep a snippet that does not exist.
        var judge = ScoredJudge(FindingsKind.Kb, Scored([0.1, 0.9, 0.95]));
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.AddRange(["s0", "s1"]);

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        Assert.Equal(new[] { "s1" }, context.KbSnippets);
    }

    [Fact]
    public async Task Kb_AllBelowThreshold_FirstAttempt_RetriesAndKeepsSnippets()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored([0.1, 0.2, 0.3, 0.4, 0.0]));
        var context = KbContext(attempts: 1);

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.KbVerification.Status);
        Assert.Equal("Judge scored no retrieved snippet at or above Verification:MinRelevance.", result.KbVerification.Reason);
        Assert.Equal(FiveSnippets, context.KbSnippets);
    }

    [Fact]
    public async Task Kb_AllBelowThreshold_SecondAttempt_FailsFinalAndClearsSnippets()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored([0.1, 0.2, 0.3, 0.4, 0.0]));
        var context = KbContext(attempts: 2);

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.KbVerification.Status);
        Assert.Empty(context.KbSnippets);
    }

    [Fact]
    public async Task Kb_NullScores_KeepsPromoteBehaviour()
    {
        // The LLM judge reports no Scores, so the old BestIndex promotion applies unchanged and nothing is dropped.
        var judge = ScoredJudge(FindingsKind.Kb, new SnippetJudgement(BestIndex: 3, Scores: null, Coverage: null, TokensUsed: 1, Source: "llm"));
        var context = KbContext();

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        Assert.Equal(new[] { "s3", "s0", "s1", "s2", "s4" }, context.KbSnippets);
        Assert.Null(result.KbVerification.Scores);
    }

    [Fact]
    public async Task Kb_CoverageBelowPartialThreshold_SetsCoverageIsPartial()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored(TwoAboveThreshold, coverage: 0.5));
        var context = KbContext();

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.True(result.KbVerification.CoverageIsPartial);
        Assert.Equal(0.5, result.KbVerification.Coverage);
    }

    [Fact]
    public async Task Kb_CoverageAtOrAbovePartialThreshold_LeavesCoverageIsPartialFalse()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored(TwoAboveThreshold, coverage: 0.9));
        var context = KbContext();

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.False(result.KbVerification.CoverageIsPartial);
    }

    [Fact]
    public async Task Kb_NullCoverage_LeavesCoverageIsPartialFalse()
    {
        var judge = ScoredJudge(FindingsKind.Kb, Scored(TwoAboveThreshold));
        var context = KbContext();

        var result = await KbVerifier(judge.Object).RunAsync(context);

        Assert.False(result.KbVerification.CoverageIsPartial);
    }

    [Fact]
    public async Task Kb_RetryWithFullCoverage_DoesNotInheritPartialFlag()
    {
        // The first attempt was partial. The retry re-judges with full coverage, so the flag must come back false.
        var context = KbContext();
        var first = ScoredJudge(FindingsKind.Kb, Scored(TwoAboveThreshold, coverage: 0.2));
        await KbVerifier(first.Object).RunAsync(context);
        Assert.True(context.KbVerification.CoverageIsPartial);

        var retry = ScoredJudge(FindingsKind.Kb, Scored([0.9], coverage: 0.95));
        var result = await KbVerifier(retry.Object).RunAsync(context);

        Assert.False(result.KbVerification.CoverageIsPartial);
    }

    [Fact]
    public async Task Code_TwoOfFiveAboveThreshold_KeepsThoseTwoInScoreOrder_DropsTheRest_Passes()
    {
        var judge = ScoredJudge(FindingsKind.Code, Scored(TwoAboveThreshold));
        var context = CodeContext();

        var result = await CodeVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.CodeVerification.Status);
        Assert.Equal(new[] { "s1", "s3" }, context.CodeSnippets);
    }

    [Fact]
    public async Task Code_AllBelowThreshold_FirstAttempt_RetriesAndKeepsSnippets()
    {
        var judge = ScoredJudge(FindingsKind.Code, Scored([0.1, 0.2, 0.3, 0.4, 0.0]));
        var context = CodeContext(attempts: 1);

        var result = await CodeVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.CodeVerification.Status);
        Assert.Equal(FiveSnippets, context.CodeSnippets);
    }

    [Fact]
    public async Task Code_AllBelowThreshold_SecondAttempt_FailsFinalAndClearsSnippets()
    {
        var judge = ScoredJudge(FindingsKind.Code, Scored([0.1, 0.2, 0.3, 0.4, 0.0]));
        var context = CodeContext(attempts: 2);

        var result = await CodeVerifier(judge.Object).RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.CodeVerification.Status);
        Assert.Empty(context.CodeSnippets);
    }

    [Fact]
    public async Task Code_CoverageBelowPartialThreshold_SetsCoverageIsPartial()
    {
        var judge = ScoredJudge(FindingsKind.Code, Scored(TwoAboveThreshold, coverage: 0.3));
        var context = CodeContext();

        var result = await CodeVerifier(judge.Object).RunAsync(context);

        Assert.True(result.CodeVerification.CoverageIsPartial);
    }

    [Fact]
    public async Task Vision_JudgeScore_IsStoredAsSingleScore_AndVerdictComesFromRelevant()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.SetupGet(j => j.JudgesAllVisionFindings).Returns(true);
        judge.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionJudgement(Relevant: true, Score: 0.8, TokensUsed: 3, Source: "jev"));
        var verifier = new VisionAnalyzerVerifier(judge.Object, new VerificationOptions(), new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", Intent = "screenshot_error", ScreenshotBase64 = "data",
            VisionFindings = new string('x', 200),
        };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.VisionVerification.Status);
        var score = Assert.Single(result.VisionVerification.Scores!);
        Assert.Equal(0.8, score);
    }

    [Fact]
    public async Task Vision_JudgeScoreWithRelevantFalse_FailsEvenWhenScoreIsHigh()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.SetupGet(j => j.JudgesAllVisionFindings).Returns(true);
        judge.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionJudgement(Relevant: false, Score: 0.9, TokensUsed: 3, Source: "jev"));
        var verifier = new VisionAnalyzerVerifier(judge.Object, new VerificationOptions(), new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", Intent = "screenshot_error", ScreenshotBase64 = "data",
            VisionFindings = new string('x', 200),
        };
        context.VisionVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.VisionVerification.Status);
    }

    [Fact]
    public async Task Vision_LegacyJudgeWithoutScore_LeavesScoresNull()
    {
        var judge = new Mock<IFindingsJudge>();
        judge.SetupGet(j => j.JudgesAllVisionFindings).Returns(false);
        judge.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionJudgement(Relevant: true, Score: null, TokensUsed: 3, Source: "llm"));
        var verifier = new VisionAnalyzerVerifier(judge.Object, new VerificationOptions(), new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", Intent = "screenshot_error", ScreenshotBase64 = "data",
            VisionFindings = "short",
        };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.VisionVerification.Status);
        Assert.Null(result.VisionVerification.Scores);
    }
}
