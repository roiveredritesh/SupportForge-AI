using SupportForge.Agents.Guard;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class GuardPolicyTests
{
    private static GuardAssessment Assessment(string source, params (string Name, double Score)[] scores) =>
        new(scores.ToDictionary(s => s.Name, s => s.Score), source);

    private static GuardAssessment Heuristic(params (string Name, double Score)[] scores) =>
        Assessment("heuristic", scores);

    private static GuardAssessment Jev(params (string Name, double Score)[] scores) =>
        Assessment("jev", scores);

    private static GuardOptions OptionsFor(GuardMode mode, bool failClosed = false) =>
        new() { Mode = mode, FailClosed = failClosed };

    [Fact]
    public void Off_ReturnsAllowedWithOffSource_EvenWhenSignalsFire()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Off),
            Heuristic((GuardSignals.OverrideInstructions, 1.0)),
            Jev((GuardSignals.OverrideInstructions, 0.99)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Allowed, decision.Verdict);
        Assert.Equal("off", decision.Source);
        Assert.Empty(decision.Signals);
        Assert.Empty(decision.FiredSignals);
    }

    [Fact]
    public void HeuristicHitOnly_InEnforce_IsFlaggedNeverBlocked()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic((GuardSignals.OverrideInstructions, 1.0)),
            jev: null,
            jevFailed: false);

        Assert.Equal(GuardVerdict.Flagged, decision.Verdict);
        Assert.Equal("heuristic", decision.Source);
        Assert.Equal(new[] { GuardSignals.OverrideInstructions }, decision.FiredSignals.ToArray());
    }

    [Fact]
    public void NoHitAndNoJev_IsAllowedWithHeuristicSource()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic(),
            jev: null,
            jevFailed: false);

        Assert.Equal(GuardVerdict.Allowed, decision.Verdict);
        Assert.Equal("heuristic", decision.Source);
        Assert.Empty(decision.FiredSignals);
    }

    [Fact]
    public void JevBlockClassAboveThreshold_InEnforce_IsBlockedWithSignalFired()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic(),
            Jev((GuardSignals.OverrideInstructions, 0.95)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Blocked, decision.Verdict);
        Assert.Equal("jev", decision.Source);
        Assert.Contains(GuardSignals.OverrideInstructions, decision.FiredSignals);
        Assert.Equal(0.95, decision.Signals[GuardSignals.OverrideInstructions]);
    }

    [Fact]
    public void JevBlockClassAboveThreshold_InShadow_IsFlagged()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Shadow),
            Heuristic(),
            Jev((GuardSignals.OverrideInstructions, 0.95)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Flagged, decision.Verdict);
        Assert.Contains(GuardSignals.OverrideInstructions, decision.FiredSignals);
    }

    [Fact]
    public void JevReviewClassAboveThreshold_InEnforce_IsFlaggedNotBlocked()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic(),
            Jev((GuardSignals.ExtractRawCode, 0.99)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Flagged, decision.Verdict);
        Assert.Equal(new[] { GuardSignals.ExtractRawCode }, decision.FiredSignals.ToArray());
    }

    [Fact]
    public void JevBlockClassBelowDefaultThreshold_IsAllowed()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic(),
            Jev((GuardSignals.OverrideInstructions, 0.85)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Allowed, decision.Verdict);
        Assert.Empty(decision.FiredSignals);
    }

    [Fact]
    public void PerSignalThresholdOverride_MakesLowerScoreFire()
    {
        var options = OptionsFor(GuardMode.Enforce);
        options.Thresholds[GuardSignals.OverrideInstructions] = 0.8;

        var decision = GuardPolicy.Decide(options,
            Heuristic(),
            Jev((GuardSignals.OverrideInstructions, 0.85)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Blocked, decision.Verdict);
        Assert.Contains(GuardSignals.OverrideInstructions, decision.FiredSignals);
    }

    [Fact]
    public void JevFailed_InEnforceWithoutFailClosed_IsUnscreened()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce, failClosed: false),
            Heuristic(),
            jev: null,
            jevFailed: true);

        Assert.Equal(GuardVerdict.Unscreened, decision.Verdict);
        Assert.Equal("jev-failed", decision.Source);
    }

    [Fact]
    public void JevFailed_InEnforceWithFailClosed_IsBlocked()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce, failClosed: true),
            Heuristic(),
            jev: null,
            jevFailed: true);

        Assert.Equal(GuardVerdict.Blocked, decision.Verdict);
        Assert.Equal("jev-failed", decision.Source);
        Assert.Empty(decision.FiredSignals);
    }

    [Fact]
    public void JevFailed_InShadowWithFailClosed_IsUnscreened()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Shadow, failClosed: true),
            Heuristic(),
            jev: null,
            jevFailed: true);

        Assert.Equal(GuardVerdict.Unscreened, decision.Verdict);
    }

    [Fact]
    public void HeuristicHitWithJevBelowThreshold_InEnforce_IsFlaggedNotBlocked()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic((GuardSignals.OverrideInstructions, 1.0)),
            Jev((GuardSignals.OverrideInstructions, 0.3)),
            jevFailed: false);

        Assert.Equal(GuardVerdict.Flagged, decision.Verdict);
        Assert.Equal(new[] { GuardSignals.OverrideInstructions }, decision.FiredSignals.ToArray());
    }

    [Fact]
    public void FiredSignals_AreSortedOrdinalAndDistinct()
    {
        var decision = GuardPolicy.Decide(OptionsFor(GuardMode.Enforce),
            Heuristic((GuardSignals.OverrideInstructions, 1.0)),
            Jev(
                (GuardSignals.ExtractSystemPrompt, 0.95),
                (GuardSignals.OverrideInstructions, 0.95),
                (GuardSignals.ExtractRawCode, 0.9)),
            jevFailed: false);

        Assert.Equal(
            new[] { GuardSignals.ExtractRawCode, GuardSignals.ExtractSystemPrompt, GuardSignals.OverrideInstructions },
            decision.FiredSignals.ToArray());
    }
}
