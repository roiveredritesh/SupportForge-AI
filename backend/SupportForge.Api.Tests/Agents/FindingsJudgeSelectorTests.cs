using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Verification;

namespace SupportForge.Api.Tests.Agents;

// U12 (JEV intent classification plan, R20 / R21): FindingsJudgeSelector routes each judgement to the LLM or the Jev judge per
// Verification:Judge, with the fallback and Shadow rules of TriageAgent (R6 / R7).
public class FindingsJudgeSelectorTests
{
    private static readonly SnippetJudgement LlmSnippets = new(BestIndex: 0, Scores: null, Coverage: null, TokensUsed: 7, Source: "llm");
    private static readonly SnippetJudgement JevSnippets = new(BestIndex: 1, Scores: new[] { 0.2, 0.9 }, Coverage: 0.5, TokensUsed: 120, Source: "jev");
    private static readonly VisionJudgement LlmVision = new(Relevant: true, Score: null, TokensUsed: 5, Source: "llm");
    private static readonly VisionJudgement JevVision = new(Relevant: true, Score: 0.8, TokensUsed: 60, Source: "jev");
    private static readonly GroundednessJudgement LlmGroundedness = new(Grounded: true, UnsupportedSentences: null, TokensUsed: 9, Source: "llm");
    private static readonly GroundednessJudgement JevGroundedness = new(Grounded: false, UnsupportedSentences: new[] { 1 }, TokensUsed: 30, Source: "jev");

    private static readonly IReadOnlyList<string> Snippets = new[] { "refunds take five days", "install guide" };

    private static AgentContext NewContext(string? vision = null) =>
        new() { ProjectId = "p1", Query = "how do refunds work?", VisionFindings = vision };

    private static Mock<IFindingsJudge> Stub(
        SnippetJudgement snippets,
        VisionJudgement vision,
        GroundednessJudgement groundedness,
        bool judgesAllVision)
    {
        var judge = new Mock<IFindingsJudge>();
        judge.Setup(j => j.JudgesAllVisionFindings).Returns(judgesAllVision);
        judge.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(snippets);
        judge.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(vision);
        judge.Setup(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(groundedness);
        return judge;
    }

    private static Mock<IFindingsJudge> Llm() => Stub(LlmSnippets, LlmVision, LlmGroundedness, judgesAllVision: false);

    private static Mock<IFindingsJudge> Jev() => Stub(JevSnippets, JevVision, JevGroundedness, judgesAllVision: true);

    private static VerificationOptions Options(FindingsJudgeMode mode) => new() { Judge = mode, JevTimeout = TimeSpan.FromMilliseconds(500) };

    private static FindingsJudgeSelector Create(
        Mock<IFindingsJudge> llm,
        Mock<IFindingsJudge>? jev,
        VerificationOptions options,
        ListLogger<FindingsJudgeSelector>? logger = null) =>
        new(llm.Object, jev?.Object, options, logger ?? new ListLogger<FindingsJudgeSelector>());

    private static void AssertJevNeverCalled(Mock<IFindingsJudge> jev)
    {
        jev.Verify(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        jev.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Never);
        jev.Verify(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LlmMode_NeverCallsJev()
    {
        var llm = Llm();
        var jev = Jev();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Llm));

        var result = await selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, Snippets, CancellationToken.None);

        Assert.Same(LlmSnippets, result);
        llm.Verify(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), FindingsKind.Kb, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        AssertJevNeverCalled(jev);
        Assert.False(selector.JudgesAllVisionFindings);
    }

    [Fact]
    public async Task JevMode_WhenJevAnswers_UsesJev()
    {
        var llm = Llm();
        var jev = Jev();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Jev));

        var result = await selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, Snippets, CancellationToken.None);

        Assert.Same(JevSnippets, result);
        Assert.Equal("jev", result.Source);
        llm.Verify(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(selector.JudgesAllVisionFindings);
    }

    [Fact]
    public async Task JevMode_WhenJevThrows_FallsBackToLlm_AndLogsTheTypeOnly()
    {
        var llm = Llm();
        var jev = Jev();
        jev.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("customer text secret-marker"));
        var logger = new ListLogger<FindingsJudgeSelector>();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Jev), logger);

        var result = await selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, Snippets, CancellationToken.None);

        Assert.Equal("llm-fallback", result.Source);
        Assert.Equal(7, result.TokensUsed);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("VerifierJevFallback", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JevMode_WhenJevIsSlowerThanTheBudget_FallsBackToLlm()
    {
        var llm = Llm();
        var jev = Jev();

        // The Jev judge ignores its token and answers after a second. The selector must not wait for it.
        jev.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
           .Returns(async () =>
           {
               await Task.Delay(TimeSpan.FromSeconds(1));
               return JevSnippets;
           });
        var logger = new ListLogger<FindingsJudgeSelector>();
        var options = new VerificationOptions { Judge = FindingsJudgeMode.Jev, JevTimeout = TimeSpan.FromMilliseconds(50) };
        var selector = Create(llm, jev, options, logger);

        var clock = Stopwatch.StartNew();
        var result = await selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, Snippets, CancellationToken.None);
        clock.Stop();

        Assert.Equal("llm-fallback", result.Source);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds}ms");
        Assert.Contains(logger.Entries, e => e.Message.Contains("VerifierJevFallback", StringComparison.Ordinal) && e.Message.Contains("TimeoutException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JevMode_CallerCancellation_PropagatesAndDoesNotFallBack()
    {
        var llm = Llm();
        var jev = Jev();
        jev.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
           .Returns<AgentContext, FindingsKind, IReadOnlyList<string>, CancellationToken>(async (c, k, s, token) =>
           {
               await Task.Delay(TimeSpan.FromSeconds(10), token);
               return JevSnippets;
           });
        var logger = new ListLogger<FindingsJudgeSelector>();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Jev), logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Kb, Snippets, cts.Token));

        llm.Verify(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("VerifierJevFallback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JevMode_CodeSnippets_WithIncludeCodeOff_NeverCallJev()
    {
        var llm = Llm();
        var jev = Jev();
        var options = new VerificationOptions
        {
            Judge = FindingsJudgeMode.Jev,
            Jev = new VerificationJevOptions { IncludeCode = false },
        };
        var selector = Create(llm, jev, options);

        var result = await selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Code, Snippets, CancellationToken.None);

        Assert.Same(LlmSnippets, result);
        Assert.Equal("llm", result.Source);
        AssertJevNeverCalled(jev);
    }

    [Fact]
    public async Task JevMode_CodeSnippets_WithIncludeCodeOn_UseJev()
    {
        var llm = Llm();
        var jev = Jev();
        var options = new VerificationOptions
        {
            Judge = FindingsJudgeMode.Jev,
            JevTimeout = TimeSpan.FromMilliseconds(500),
            Jev = new VerificationJevOptions { IncludeCode = true },
        };
        var selector = Create(llm, jev, options);

        var result = await selector.JudgeSnippetsAsync(NewContext(), FindingsKind.Code, Snippets, CancellationToken.None);

        Assert.Equal("jev", result.Source);
    }

    [Fact]
    public async Task JevMode_VisionWhenJevAnswers_UsesJev()
    {
        var llm = Llm();
        var jev = Jev();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Jev));

        var result = await selector.JudgeVisionAsync(NewContext(vision: "Blank screen"), CancellationToken.None);

        Assert.Same(JevVision, result);
        llm.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_VisionFallback_LongFindings_AutoPassWithoutLlmCall()
    {
        var llm = Llm();
        var jev = Jev();
        jev.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("slow"));
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Jev));

        var result = await selector.JudgeVisionAsync(NewContext(vision: new string('e', 25) + " error dialog"), CancellationToken.None);

        Assert.True(result.Relevant);
        Assert.Null(result.Score);
        Assert.Equal(0, result.TokensUsed);
        Assert.Equal("llm-fallback", result.Source);
        llm.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_VisionFallback_ShortFindings_GoToTheLlm()
    {
        var llm = Llm();
        var jev = Jev();
        jev.Setup(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("slow"));
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Jev));

        var result = await selector.JudgeVisionAsync(NewContext(vision: "Blank screen"), CancellationToken.None);

        Assert.Equal("llm-fallback", result.Source);
        Assert.Equal(5, result.TokensUsed);
        llm.Verify(j => j.JudgeVisionAsync(It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JevMode_GroundednessUsesJevOnlyWhenPerSentenceIsOn()
    {
        var llm = Llm();
        var jev = Jev();
        var selector = Create(llm, jev, new VerificationOptions { Judge = FindingsJudgeMode.Jev, GroundednessPerSentence = false });

        var result = await selector.JudgeGroundednessAsync(NewContext(), "The refund takes five days to arrive.", CancellationToken.None);

        Assert.Same(LlmGroundedness, result);
        AssertJevNeverCalled(jev);
    }

    [Fact]
    public async Task JevMode_GroundednessWithPerSentenceOn_UsesJev_AndFallsBackOnFailure()
    {
        var jevAnswers = Jev();
        var selectorJev = Create(Llm(), jevAnswers, new VerificationOptions
        {
            Judge = FindingsJudgeMode.Jev,
            GroundednessPerSentence = true,
            JevTimeout = TimeSpan.FromMilliseconds(500),
        });
        var judged = await selectorJev.JudgeGroundednessAsync(NewContext(), "The refund takes five days to arrive.", CancellationToken.None);
        Assert.Same(JevGroundedness, judged);

        var failing = Jev();
        failing.Setup(j => j.JudgeGroundednessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("boom"));
        var selectorFailing = Create(Llm(), failing, new VerificationOptions
        {
            Judge = FindingsJudgeMode.Jev,
            GroundednessPerSentence = true,
            JevTimeout = TimeSpan.FromMilliseconds(500),
        });
        var fallback = await selectorFailing.JudgeGroundednessAsync(NewContext(), "The refund takes five days to arrive.", CancellationToken.None);
        Assert.Equal("llm-fallback", fallback.Source);
    }

    [Fact]
    public async Task ShadowMode_ActsOnLlm_LogsTheJevAnswer_AndRecordsItsTokens()
    {
        var llm = Llm();
        var jev = Jev();
        var logger = new ListLogger<FindingsJudgeSelector>();
        var context = NewContext();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Shadow), logger);

        var result = await selector.JudgeSnippetsAsync(context, FindingsKind.Kb, Snippets, CancellationToken.None);

        Assert.Same(LlmSnippets, result);
        Assert.Equal("llm", result.Source);
        jev.Verify(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), FindingsKind.Kb, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(120, context.TokensByAgent["Verification.Shadow"]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("VerifierShadow", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("jevFailed=False", StringComparison.Ordinal));
        Assert.False(selector.JudgesAllVisionFindings);
    }

    [Fact]
    public async Task ShadowMode_WhenJevFails_DoesNotThrow_AndLogsJevFailed()
    {
        var llm = Llm();
        var jev = Jev();
        jev.Setup(j => j.JudgeSnippetsAsync(It.IsAny<AgentContext>(), It.IsAny<FindingsKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("boom"));
        var logger = new ListLogger<FindingsJudgeSelector>();
        var context = NewContext();
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Shadow), logger);

        var result = await selector.JudgeSnippetsAsync(context, FindingsKind.Kb, Snippets, CancellationToken.None);

        Assert.Same(LlmSnippets, result);
        Assert.Contains(logger.Entries, e => e.Message.Contains("jevFailed=True", StringComparison.Ordinal));
        Assert.False(context.TokensByAgent.ContainsKey("Verification.Shadow"));
    }

    [Fact]
    public async Task ShadowMode_Vision_ActsOnLlm_AndLogsKindVision()
    {
        var llm = Llm();
        var jev = Jev();
        var logger = new ListLogger<FindingsJudgeSelector>();
        var context = NewContext(vision: "Blank screen");
        var selector = Create(llm, jev, Options(FindingsJudgeMode.Shadow), logger);

        var result = await selector.JudgeVisionAsync(context, CancellationToken.None);

        Assert.Same(LlmVision, result);
        Assert.Contains(logger.Entries, e => e.Message.Contains("VerifierShadow", StringComparison.Ordinal) && e.Message.Contains("kind=vision", StringComparison.Ordinal));
        Assert.Equal(60, context.TokensByAgent["Verification.Shadow"]);
    }

    [Fact]
    public async Task ShadowMode_Groundedness_IsLlmOnly()
    {
        var llm = Llm();
        var jev = Jev();
        var selector = Create(llm, jev, new VerificationOptions { Judge = FindingsJudgeMode.Shadow, GroundednessPerSentence = true });

        var result = await selector.JudgeGroundednessAsync(NewContext(), "The refund takes five days to arrive.", CancellationToken.None);

        Assert.Same(LlmGroundedness, result);
        AssertJevNeverCalled(jev);
    }

    [Theory]
    [InlineData(FindingsJudgeMode.Jev)]
    [InlineData(FindingsJudgeMode.Shadow)]
    public void JevAndShadowModes_RequireAJevJudge(FindingsJudgeMode mode)
    {
        var options = new VerificationOptions { Judge = mode };

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new FindingsJudgeSelector(Llm().Object, null, options, new ListLogger<FindingsJudgeSelector>());
        });
    }

    [Theory]
    [InlineData(FindingsJudgeMode.Llm, false)]
    [InlineData(FindingsJudgeMode.Jev, true)]
    [InlineData(FindingsJudgeMode.Shadow, false)]
    public void JudgesAllVisionFindingsFollowsTheMode(FindingsJudgeMode mode, bool expected)
    {
        var selector = Create(Llm(), mode == FindingsJudgeMode.Llm ? null : Jev(), new VerificationOptions { Judge = mode });

        Assert.Equal(expected, selector.JudgesAllVisionFindings);
    }
}
