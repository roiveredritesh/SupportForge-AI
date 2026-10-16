using SupportForge.Agents;

namespace SupportForge.Api.Tests.Agents;

// U7: IntentRouting must reproduce the inline gates exactly while secondary routing is disabled, and
// the opt-in secondary rule must only ever widen retrieval.
public class IntentRoutingTests
{
    private static AgentContext Build(
        string intent,
        IReadOnlyDictionary<string, double>? scores = null,
        bool secondary = false,
        double minProbability = 0.35) => new()
    {
        ProjectId = "p",
        Query = "q",
        Intent = intent,
        IntentScores = scores ?? new Dictionary<string, double>(),
        SecondaryIntentEnabled = secondary,
        SecondaryMinProbability = minProbability,
    };

    // Expected values are the pre-U7 inline gates: KB branch for kb_question/code_issue/code_question,
    // code branch for code_issue/code_question, nothing for every other label.
    [Theory]
    [InlineData("kb_question", true, false)]
    [InlineData("code_issue", true, true)]
    [InlineData("code_question", true, true)]
    [InlineData("screenshot_error", false, false)]
    [InlineData("greeting", false, false)]
    [InlineData("unclear", false, false)]
    [InlineData("bogus", false, false)]
    [InlineData("", false, false)]
    public void PrimaryIntent_WithSecondaryDisabled_MatchesLegacyGates(string intent, bool expectKb, bool expectCode)
    {
        var context = Build(intent, new Dictionary<string, double> { [intent] = 0.9, ["kb_question"] = 0.5 });

        Assert.Equal(expectKb, IntentRouting.NeedsKb(context));
        Assert.Equal(expectCode, IntentRouting.NeedsCode(context));
    }

    [Fact]
    public void SecondaryEnabled_ConfidentRunnerUpAddsBranch()
    {
        var scores = new Dictionary<string, double> { ["kb_question"] = 0.55, ["code_question"] = 0.40 };
        var context = Build("kb_question", scores, secondary: true);

        Assert.True(IntentRouting.NeedsCode(context));
        Assert.True(IntentRouting.NeedsKb(context));
    }

    [Fact]
    public void SecondaryEnabled_RunnerUpBelowThreshold_DoesNotAddBranch()
    {
        var scores = new Dictionary<string, double> { ["kb_question"] = 0.55, ["code_question"] = 0.20 };
        var context = Build("kb_question", scores, secondary: true);

        Assert.False(IntentRouting.NeedsCode(context));
        Assert.True(IntentRouting.NeedsKb(context));
    }

    [Fact]
    public void SecondaryDisabled_ConfidentRunnerUpIgnored()
    {
        var scores = new Dictionary<string, double> { ["kb_question"] = 0.55, ["code_question"] = 0.40 };
        var context = Build("kb_question", scores, secondary: false);

        Assert.False(IntentRouting.NeedsCode(context));
    }

    [Fact]
    public void Unclear_NeverUsesSecondaryIntent()
    {
        var scores = new Dictionary<string, double> { ["unclear"] = 0.1, ["code_issue"] = 0.9 };
        var context = Build("unclear", scores, secondary: true);

        Assert.False(IntentRouting.NeedsCode(context));
        Assert.False(IntentRouting.NeedsKb(context));
    }

    [Fact]
    public void Greeting_NeverUsesSecondaryIntent()
    {
        var scores = new Dictionary<string, double> { ["greeting"] = 0.1, ["kb_question"] = 0.9 };
        var context = Build("greeting", scores, secondary: true);

        Assert.False(IntentRouting.NeedsKb(context));
    }

    [Fact]
    public void ScreenshotError_RunnerUpKbQuestionAddsKbOnly()
    {
        var scores = new Dictionary<string, double> { ["screenshot_error"] = 0.7, ["kb_question"] = 0.5 };
        var context = Build("screenshot_error", scores, secondary: true);

        Assert.True(IntentRouting.NeedsKb(context));
        Assert.False(IntentRouting.NeedsCode(context));
    }

    [Fact]
    public void CustomThreshold_RunnerUpBelowRaisedMinimum_DoesNotAddBranch()
    {
        var scores = new Dictionary<string, double> { ["kb_question"] = 0.55, ["code_question"] = 0.45 };
        var context = Build("kb_question", scores, secondary: true, minProbability: 0.5);

        Assert.False(IntentRouting.NeedsCode(context));
    }

    // Widen-only invariant: a runner-up that points at a different branch must never switch off the
    // branch the primary intent already needs.
    [Fact]
    public void SecondaryIntent_NeverRemovesPrimaryBranch()
    {
        var scores = new Dictionary<string, double> { ["code_issue"] = 0.6, ["kb_question"] = 0.9 };
        var context = Build("code_issue", scores, secondary: true);

        Assert.True(IntentRouting.NeedsCode(context));
        Assert.True(IntentRouting.NeedsKb(context));
    }
}
