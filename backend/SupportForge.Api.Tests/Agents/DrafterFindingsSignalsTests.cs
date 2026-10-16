using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Verification;
using SupportForge.Core;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U13 (R16, R18, R19): the Drafter reads the findings signals. Coverage adds a prompt hint, confidence blends scores and coverage,
// and unsupported sentences are logged by index.
public class DrafterFindingsSignalsTests
{
    private const string CoverageHint =
        "The retrieved context only partly answers this question: answer the covered part and say plainly which part you could not find.";

    private const string CleanDraft = "This is expected: empty carts are rejected before payment, so nothing was charged.";

    private static AgentContext NewContext() => new()
    {
        ProjectId = "p",
        Query = "why does checkout fail",
        Intent = "kb_question",
    };

    // ---- R16: coverage hint -------------------------------------------------

    [Fact]
    public void PassedKbBranchWithPartialCoverage_AddsHintLine_JustBeforeFreshness()
    {
        var context = NewContext();
        context.KbSnippets.Add("Carts reject empty checkouts.");
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.CoverageIsPartial = true;

        var lines = DrafterAgent.BuildUserPrompt(context).Split('\n');
        var freshnessIndex = Array.FindIndex(lines, l => l.StartsWith("Freshness:", StringComparison.Ordinal));

        Assert.True(freshnessIndex > 0);
        Assert.Equal(CoverageHint, lines[freshnessIndex - 1]);
    }

    [Fact]
    public void PassedCodeBranchWithPartialCoverage_AddsHintLine()
    {
        var context = NewContext();
        context.CodeSnippets.Add("Checkout validation helper.");
        context.CodeVerification.Status = VerificationStatus.Passed;
        context.CodeVerification.CoverageIsPartial = true;

        Assert.Contains(CoverageHint, DrafterAgent.BuildUserPrompt(context));
    }

    [Fact]
    public void PartialCoverageOnFailedBranch_AddsNoHintLine()
    {
        var context = NewContext();
        context.KbSnippets.Add("Carts reject empty checkouts.");
        context.KbVerification.Status = VerificationStatus.FailedFinal;
        context.KbVerification.CoverageIsPartial = true;

        Assert.DoesNotContain(CoverageHint, DrafterAgent.BuildUserPrompt(context));
    }

    [Fact]
    public void PassedBranchWithFullCoverage_AddsNoHintLine()
    {
        var context = NewContext();
        context.KbSnippets.Add("Carts reject empty checkouts.");
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.CoverageIsPartial = false;

        Assert.DoesNotContain(CoverageHint, DrafterAgent.BuildUserPrompt(context));
    }

    [Fact]
    public void PartialCoverageHint_SitsAfterIntentConfidenceLine()
    {
        var context = NewContext();
        context.IntentConfidenceBand = "medium";
        context.KbSnippets.Add("Carts reject empty checkouts.");
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.CoverageIsPartial = true;

        var lines = DrafterAgent.BuildUserPrompt(context).Split('\n');
        var hintIndex = Array.FindIndex(lines, l => l == CoverageHint);
        var intentIndex = Array.FindIndex(lines, l => l.StartsWith("Intent confidence: medium.", StringComparison.Ordinal));

        Assert.True(intentIndex >= 0);
        Assert.Equal(intentIndex + 1, hintIndex);
    }

    // ---- R19: score-based confidence ----------------------------------------

    [Fact]
    public void ScoredBranch_TopScoreTimesCoverage_BlendsIntoConfidence()
    {
        // Top score 0.9 times coverage 0.5 is 0.45 for the single applicable branch.
        var context = NewContext();
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.Scores = new[] { 0.9, 0.3 };
        context.KbVerification.Coverage = 0.5;

        Assert.Equal(0.2 + 0.7 * 0.45, DrafterAgent.ComputeConfidence(context), 10);
    }

    [Fact]
    public void ScoredBranch_WithoutCoverage_UsesTopScoreAlone()
    {
        var context = NewContext();
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.Scores = new[] { 0.6 };

        Assert.Equal(0.2 + 0.7 * 0.6, DrafterAgent.ComputeConfidence(context), 10);
    }

    [Fact]
    public void ScoredPassedBranchAndFailedBranch_AveragesBranchScores()
    {
        // KB scores 0.45 and the failed Code branch scores 0, so the mean is 0.225.
        var context = NewContext();
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.Scores = new[] { 0.9 };
        context.KbVerification.Coverage = 0.5;
        context.CodeVerification.Status = VerificationStatus.FailedFinal;

        Assert.Equal(0.2 + 0.7 * 0.225, DrafterAgent.ComputeConfidence(context), 10);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void LlmOnlyBranches_ConfidenceEqualsOldPassedCountFormula(int passed, int failed)
    {
        // Without Scores every applicable branch scores 0 or 1, so the result must match the old 0.2 + 0.7 * passed / count exactly.
        var context = NewContext();
        var branches = new[] { context.KbVerification, context.CodeVerification, context.VisionVerification };
        var index = 0;
        for (var i = 0; i < passed; i++) branches[index++].Status = VerificationStatus.Passed;
        for (var i = 0; i < failed; i++) branches[index++].Status = VerificationStatus.FailedFinal;

        var expected = 0.2 + 0.7 * passed / (passed + failed);

        Assert.Equal(expected, DrafterAgent.ComputeConfidence(context));
    }

    [Fact]
    public void NoApplicableBranches_StillReturnsLowDefault()
    {
        Assert.Equal(0.3, DrafterAgent.ComputeConfidence(NewContext()));
    }

    [Fact]
    public void StaleFreshness_CapsScoredConfidence()
    {
        var context = NewContext();
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.Scores = new[] { 1.0 };
        context.Freshness = new FreshnessContext(SyncInProgress: false, Score: new FreshnessScore(IsFresh: false, [], []));

        Assert.Equal(0.5, DrafterAgent.ComputeConfidence(context));
    }

    [Fact]
    public void FreshSyncIdleFreshness_DoesNotCap()
    {
        var context = NewContext();
        context.KbVerification.Status = VerificationStatus.Passed;
        context.KbVerification.Scores = new[] { 1.0 };
        context.Freshness = new FreshnessContext(SyncInProgress: false, Score: new FreshnessScore(IsFresh: true, [], []));

        Assert.Equal(0.2 + 0.7 * 1.0, DrafterAgent.ComputeConfidence(context), 10);
    }

    // ---- R18: unsupported sentences -----------------------------------------

    [Fact]
    public async Task UnsupportedSentences_AreLoggedByIndex_AndHalveConfidence()
    {
        var (context, logger) = await RunDrafterAsync(new GroundednessJudgement(Grounded: false, UnsupportedSentences: new[] { 1, 3 }, TokensUsed: 2, Source: "jev"));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("DrafterUnsupportedSentences", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("indexes=1,3", entry.Message, StringComparison.Ordinal);
        Assert.Equal(DrafterAgent.ComputeConfidence(context) * 0.5, context.Confidence);
    }

    [Fact]
    public async Task AllSentencesSupported_LogsNothingAndKeepsConfidence()
    {
        var (context, logger) = await RunDrafterAsync(new GroundednessJudgement(Grounded: true, UnsupportedSentences: Array.Empty<int>(), TokensUsed: 2, Source: "jev"));

        Assert.Empty(logger.Entries.Where(e => e.Message.Contains("DrafterUnsupportedSentences", StringComparison.Ordinal)));
        Assert.Equal(DrafterAgent.ComputeConfidence(context), context.Confidence);
    }

    // Runs the Drafter once with a mocked draft LLM and a mocked groundedness judge. Returns the context and the logger.
    private static async Task<(AgentContext Context, ListLogger<DrafterAgent> Logger)> RunDrafterAsync(GroundednessJudgement judgement)
    {
        var context = NewContext();
        context.KbSnippets.Add("Carts reject empty checkouts.");
        context.KbVerification.Status = VerificationStatus.Passed;
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(CleanDraft);
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(judgement);
        var logger = new ListLogger<DrafterAgent>();

        await new DrafterAgent(llm.Object, logger, groundednessCheckEnabled: true, judge: judge.Object).RunAsync(context);

        return (context, logger);
    }
}
