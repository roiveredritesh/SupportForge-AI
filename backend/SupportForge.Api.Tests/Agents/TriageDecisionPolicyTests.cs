using SupportForge.Agents.Triage;

namespace SupportForge.Api.Tests.Agents;

// U5 (JEV intent classification plan): TriageDecisionPolicy is a pure function, so each rule of R4 and KTD3 gets its own
// case. The LLM pass-through cases matter most: the policy must never change a decision the LLM path already made.
public class TriageDecisionPolicyTests
{
    private static IntentDecision Decision(
        double confidence,
        Dictionary<string, double> scores,
        Dictionary<string, double>? signals = null) =>
        new(scores, confidence, signals ?? new Dictionary<string, double>(), 0, "jev");

    private static TriagePolicyResult Decide(IntentDecision decision, bool hasScreenshot = false) =>
        TriageDecisionPolicy.Decide(decision, hasScreenshot, new TriageOptions());

    // LLM decisions: one label at 1.0, confidence 1.0, no signals. Every label must come back unchanged.
    [Theory]
    [InlineData(TriageLabels.KbQuestion)]
    [InlineData(TriageLabels.CodeIssue)]
    [InlineData(TriageLabels.CodeQuestion)]
    [InlineData(TriageLabels.ScreenshotError)]
    [InlineData(TriageLabels.Greeting)]
    [InlineData(TriageLabels.Unclear)]
    public void LlmStyleSingleLabel_PassesThroughUnchanged(string label)
    {
        var decision = new IntentDecision(
            new Dictionary<string, double> { [label] = 1.0 },
            1.0,
            new Dictionary<string, double>(),
            0,
            "llm");

        var withoutScreenshot = Decide(decision, hasScreenshot: false);
        var withScreenshot = Decide(decision, hasScreenshot: true);

        Assert.Equal(label, withoutScreenshot.Intent);
        Assert.Equal("top", withoutScreenshot.AppliedRule);
        Assert.Equal(label, withScreenshot.Intent);
    }

    [Fact]
    public void HighConfidenceKbQuestion_PassesThrough()
    {
        var decision = Decision(0.92, new Dictionary<string, double>
        {
            [TriageLabels.KbQuestion] = 0.9,
            [TriageLabels.CodeQuestion] = 0.05,
            [TriageLabels.Greeting] = 0.05,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.KbQuestion, result.Intent);
        Assert.Equal("top", result.AppliedRule);
    }

    [Fact]
    public void ConfidenceExactlyAtMinimum_IsAccepted()
    {
        var decision = Decision(0.6, new Dictionary<string, double>
        {
            [TriageLabels.KbQuestion] = 0.9,
            [TriageLabels.CodeQuestion] = 0.05,
        });

        Assert.Equal(TriageLabels.KbQuestion, Decide(decision).Intent);
    }

    [Fact]
    public void LowConfidence_ReturnsUnclear()
    {
        var decision = Decision(0.4, new Dictionary<string, double>
        {
            [TriageLabels.KbQuestion] = 0.9,
            [TriageLabels.CodeQuestion] = 0.05,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("low-confidence", result.AppliedRule);
    }

    [Fact]
    public void NaNConfidence_FailsClosedToUnclear()
    {
        var decision = Decision(double.NaN, new Dictionary<string, double> { [TriageLabels.KbQuestion] = 0.9 });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("low-confidence", result.AppliedRule);
    }

    [Fact]
    public void SmallMarginBetweenKbAndCodeQuestion_ReturnsUnclear()
    {
        var decision = Decision(0.9, new Dictionary<string, double>
        {
            [TriageLabels.KbQuestion] = 0.45,
            [TriageLabels.CodeQuestion] = 0.40,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("small-margin", result.AppliedRule);
    }

    // Rule 3: the code pair is within MinMargin, so reports_malfunction decides. It must never become "unclear".
    [Theory]
    [InlineData(0.9, TriageLabels.CodeIssue)]
    [InlineData(0.5, TriageLabels.CodeIssue)]
    [InlineData(0.1, TriageLabels.CodeQuestion)]
    public void CodePairWithinMargin_IsResolvedByReportsMalfunction(double malfunction, string expected)
    {
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.CodeIssue] = 0.46,
                [TriageLabels.CodeQuestion] = 0.44,
            },
            new Dictionary<string, double> { [TriageSignals.ReportsMalfunction] = malfunction });

        var result = Decide(decision);

        Assert.Equal(expected, result.Intent);
        Assert.Equal("code-pair-by-malfunction", result.AppliedRule);
    }

    [Fact]
    public void CodePairWithinMarginWithoutSignal_KeepsOriginalTop()
    {
        var decision = Decision(0.9, new Dictionary<string, double>
        {
            [TriageLabels.CodeIssue] = 0.46,
            [TriageLabels.CodeQuestion] = 0.44,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.CodeIssue, result.Intent);
        Assert.Equal("top", result.AppliedRule);
    }

    [Fact]
    public void CodePairWithinMargin_StillSubjectToLowConfidence()
    {
        var decision = Decision(
            0.4,
            new Dictionary<string, double>
            {
                [TriageLabels.CodeIssue] = 0.46,
                [TriageLabels.CodeQuestion] = 0.44,
            },
            new Dictionary<string, double> { [TriageSignals.ReportsMalfunction] = 0.9 });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("low-confidence", result.AppliedRule);
    }

    [Fact]
    public void TiedCodeLabelsWithoutSignal_FollowTriageLabelsOrder()
    {
        // Equal probabilities are settled by TriageLabels.All, where code_issue comes before code_question.
        var decision = Decision(0.9, new Dictionary<string, double>
        {
            [TriageLabels.CodeQuestion] = 0.7,
            [TriageLabels.CodeIssue] = 0.7,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.CodeIssue, result.Intent);
        Assert.Equal("top", result.AppliedRule);
    }

    // Rule 1: a greeting that also contains a support question moves to the best other label, then faces rule 4.
    [Fact]
    public void GreetingWithSupportQuestion_MovesToNextLabel()
    {
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.Greeting] = 0.6,
                [TriageLabels.KbQuestion] = 0.3,
                [TriageLabels.CodeQuestion] = 0.1,
            },
            new Dictionary<string, double> { [TriageSignals.HasSupportQuestion] = 0.9 });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.KbQuestion, result.Intent);
        Assert.Equal("greeting-has-question", result.AppliedRule);
    }

    [Fact]
    public void GreetingWithSupportQuestion_ThenFailsMarginCheck_ReturnsUnclear()
    {
        // The switch to kb_question still has to clear rule 4. Here it doesn't: 0.3 against 0.25 is a 0.05 margin.
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.Greeting] = 0.6,
                [TriageLabels.KbQuestion] = 0.3,
                [TriageLabels.CodeQuestion] = 0.25,
            },
            new Dictionary<string, double> { [TriageSignals.HasSupportQuestion] = 0.9 });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("small-margin", result.AppliedRule);
    }

    [Fact]
    public void GreetingWithoutSupportQuestion_StaysGreeting()
    {
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.Greeting] = 0.8,
                [TriageLabels.KbQuestion] = 0.15,
            },
            new Dictionary<string, double> { [TriageSignals.HasSupportQuestion] = 0.1 });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Greeting, result.Intent);
        Assert.Equal("top", result.AppliedRule);
    }

    [Fact]
    public void GreetingWithSupportQuestionAndNoOtherLabel_ReturnsUnclear()
    {
        var decision = Decision(
            1.0,
            new Dictionary<string, double> { [TriageLabels.Greeting] = 1.0 },
            new Dictionary<string, double> { [TriageSignals.HasSupportQuestion] = 0.9 });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("greeting-has-question", result.AppliedRule);
    }

    // Rule 1 must not fall through to screenshot_error when no image is usable, since nothing could answer it.
    [Fact]
    public void GreetingWithSupportQuestion_DoesNotPickScreenshotWithoutImage()
    {
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.Greeting] = 0.6,
                [TriageLabels.ScreenshotError] = 0.3,
            },
            new Dictionary<string, double>
            {
                [TriageSignals.HasSupportQuestion] = 0.9,
                [TriageSignals.RefersToImage] = 0.1,
            });

        var result = Decide(decision, hasScreenshot: false);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("greeting-has-question", result.AppliedRule);
    }

    // Rule 2: a screenshot question with no screenshot attached moves to the next label.
    [Fact]
    public void ScreenshotErrorWithoutImage_MovesToNextLabel()
    {
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.ScreenshotError] = 0.7,
                [TriageLabels.KbQuestion] = 0.2,
            },
            new Dictionary<string, double> { [TriageSignals.RefersToImage] = 0.1 });

        var result = Decide(decision, hasScreenshot: false);

        Assert.Equal(TriageLabels.KbQuestion, result.Intent);
        Assert.Equal("screenshot-without-image", result.AppliedRule);
    }

    [Fact]
    public void ScreenshotErrorWithAttachedImage_IsKept()
    {
        var decision = Decision(
            0.9,
            new Dictionary<string, double>
            {
                [TriageLabels.ScreenshotError] = 0.8,
                [TriageLabels.KbQuestion] = 0.1,
            },
            new Dictionary<string, double> { [TriageSignals.RefersToImage] = 0.1 });

        var result = Decide(decision, hasScreenshot: true);

        Assert.Equal(TriageLabels.ScreenshotError, result.Intent);
        Assert.Equal("top", result.AppliedRule);
    }

    [Fact]
    public void ScreenshotErrorWithoutRefersToImageSignal_IsKept()
    {
        // A missing signal is absent, so rule 2 doesn't apply.
        var decision = Decision(0.9, new Dictionary<string, double>
        {
            [TriageLabels.ScreenshotError] = 0.8,
            [TriageLabels.KbQuestion] = 0.1,
        });

        var result = Decide(decision, hasScreenshot: false);

        Assert.Equal(TriageLabels.ScreenshotError, result.Intent);
        Assert.Equal("top", result.AppliedRule);
    }

    [Fact]
    public void UnknownTopLabel_ReturnsUnclear()
    {
        var decision = Decision(0.9, new Dictionary<string, double>
        {
            ["bogus_label"] = 0.9,
            [TriageLabels.KbQuestion] = 0.05,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("unknown-label", result.AppliedRule);
    }

    [Fact]
    public void EmptyScores_ReturnsUnclear()
    {
        var decision = Decision(0.9, new Dictionary<string, double>());

        var result = Decide(decision);

        Assert.Equal(TriageLabels.Unclear, result.Intent);
        Assert.Equal("unknown-label", result.AppliedRule);
    }

    // Rule 4b: the django template question as Jev scored it.
    [Fact]
    public void LowConfidenceCodeQuestion_AboutSourceCode_IsAnsweredNotClarified()
    {
        var decision = Decision(0.24, new Dictionary<string, double>
        {
            [TriageLabels.CodeQuestion] = 0.39,
            [TriageLabels.CodeIssue] = 0.32,
            [TriageLabels.KbQuestion] = 0.29,
        }, new Dictionary<string, double>
        {
            [TriageSignals.HasSupportQuestion] = 0.95,
            [TriageSignals.AboutSourceCode] = 0.8,
            [TriageSignals.ReportsMalfunction] = 0.1,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.CodeQuestion, result.Intent);
        Assert.Equal("code-by-signal", result.AppliedRule);
    }

    [Fact]
    public void SmallMarginKbVersusCode_AboutSourceCode_ReportingAFault_IsACodeIssue()
    {
        var decision = Decision(0.7, new Dictionary<string, double>
        {
            [TriageLabels.KbQuestion] = 0.52,
            [TriageLabels.CodeQuestion] = 0.46,
            [TriageLabels.Greeting] = 0.02,
        }, new Dictionary<string, double>
        {
            [TriageSignals.HasSupportQuestion] = 0.9,
            [TriageSignals.AboutSourceCode] = 0.7,
            [TriageSignals.ReportsMalfunction] = 0.8,
        });

        var result = Decide(decision);

        Assert.Equal(TriageLabels.CodeIssue, result.Intent);
        Assert.Equal("code-by-signal", result.AppliedRule);
    }

    [Theory]
    [InlineData(0.2, 0.9)]  // not about source code ("it broke")
    [InlineData(0.8, 0.2)]  // no actual support question
    public void LowConfidence_WithoutBothSignals_StaysUnclear(double aboutSourceCode, double hasSupportQuestion)
    {
        var decision = Decision(0.3, new Dictionary<string, double>
        {
            [TriageLabels.CodeIssue] = 0.4,
            [TriageLabels.KbQuestion] = 0.35,
            [TriageLabels.Greeting] = 0.25,
        }, new Dictionary<string, double>
        {
            [TriageSignals.HasSupportQuestion] = hasSupportQuestion,
            [TriageSignals.AboutSourceCode] = aboutSourceCode,
        });

        Assert.Equal(TriageLabels.Unclear, Decide(decision).Intent);
    }

    [Fact]
    public void LowConfidence_MostlyOnNonRetrievalLabels_StaysUnclear()
    {
        var decision = Decision(0.3, new Dictionary<string, double>
        {
            [TriageLabels.CodeQuestion] = 0.35,
            [TriageLabels.Greeting] = 0.33,
            [TriageLabels.ScreenshotError] = 0.32,
        }, new Dictionary<string, double>
        {
            [TriageSignals.HasSupportQuestion] = 0.9,
            [TriageSignals.AboutSourceCode] = 0.9,
        });

        Assert.Equal(TriageLabels.Unclear, Decide(decision).Intent);
    }
}
