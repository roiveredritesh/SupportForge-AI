using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U6 (JEV intent classification plan): TriageAgent's classifier modes. Both sides are IIntentClassifier mocks, so these tests
// cover the agent's selection, fallback, Shadow and telemetry logic. The Jev wire format is covered by JevClientTests, and the
// policy rules by TriageDecisionPolicyTests.
public class TriageAgentModesTests
{
    private const string Query = "my invoice export 4417 never finishes";

    private static IntentDecision LlmDecision(string label, int tokens = 0) =>
        new(new Dictionary<string, double> { [label] = 1.0 }, 1.0, new Dictionary<string, double>(), tokens, "llm");

    private static IntentDecision JevDecision(
        IReadOnlyDictionary<string, double> scores,
        double confidence,
        int tokens = 0,
        IReadOnlyDictionary<string, double>? signals = null) =>
        new(scores, confidence, signals ?? new Dictionary<string, double>(), tokens, "jev");

    private static IReadOnlyDictionary<string, double> KbLeaning() => new Dictionary<string, double>
    {
        [TriageLabels.KbQuestion] = 0.9,
        [TriageLabels.CodeQuestion] = 0.05,
        [TriageLabels.Greeting] = 0.05,
    };

    private static IReadOnlyDictionary<string, double> CodeIssueLeaning() => new Dictionary<string, double>
    {
        [TriageLabels.CodeIssue] = 0.9,
        [TriageLabels.KbQuestion] = 0.05,
        [TriageLabels.CodeQuestion] = 0.05,
    };

    private static Mock<IIntentClassifier> Classifier(IntentDecision decision)
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(decision);
        return classifier;
    }

    private static Mock<IIntentClassifier> FailingClassifier(Exception exception)
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(exception);
        return classifier;
    }

    // Answers after `delay`, and honours its token, so cancelling the token ends the call early.
    private static Mock<IIntentClassifier> SlowCooperativeClassifier(TimeSpan delay, IntentDecision answer)
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .Returns(async (AgentContext context, IReadOnlyList<string> labels, CancellationToken token) =>
                  {
                      await Task.Delay(delay, token);
                      return answer;
                  });
        return classifier;
    }

    // Answers after `delay` and ignores its token, so only the agent's own budget can end the wait.
    private static Mock<IIntentClassifier> SlowUncooperativeClassifier(TimeSpan delay, IntentDecision answer)
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .Returns(async (AgentContext context, IReadOnlyList<string> labels, CancellationToken token) =>
                  {
                      await Task.Delay(delay);
                      return answer;
                  });
        return classifier;
    }

    private static bool HasWarning(ListLogger<TriageAgent> logger, string prefix) =>
        logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task LlmMode_NeverCallsJevClassifier_AndKeepsLlmSemantics()
    {
        var llm = Classifier(LlmDecision("kb_question", tokens: 12));
        var jev = Classifier(JevDecision(KbLeaning(), 0.95));
        var options = new TriageOptions { Classifier = TriageClassifierMode.Llm, SecondaryIntentRouting = true };
        var agent = new TriageAgent(llm.Object, jev.Object, options, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = Query };

        var result = await agent.RunAsync(context);

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("llm", result.IntentSource);
        Assert.Equal("top", result.IntentRule);
        Assert.Null(result.IntentConfidenceBand);
        Assert.False(result.SecondaryIntentEnabled);
        Assert.Equal(12, result.TokensByAgent["Triage"]);
        Assert.False(result.TokensByAgent.ContainsKey("Triage.Shadow"));
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_ConfidentDecision_ActsOnJevWithHighBand()
    {
        var llm = Classifier(LlmDecision("code_issue"));
        var jev = Classifier(JevDecision(KbLeaning(), 0.92, tokens: 80));
        var options = new TriageOptions { Classifier = TriageClassifierMode.Jev, SecondaryIntentRouting = true };
        var agent = new TriageAgent(llm.Object, jev.Object, options, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = Query };

        var result = await agent.RunAsync(context);

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("jev", result.IntentSource);
        Assert.Equal("top", result.IntentRule);
        Assert.Equal("high", result.IntentConfidenceBand);
        Assert.Equal(0.92, result.IntentConfidence);
        Assert.True(result.SecondaryIntentEnabled);
        Assert.Equal(options.SecondaryMinProbability, result.SecondaryMinProbability);
        Assert.Equal(80, result.TokensByAgent["Triage"]);
        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_MediumConfidence_GetsMediumBand()
    {
        var llm = Classifier(LlmDecision("code_issue"));
        var jev = Classifier(JevDecision(KbLeaning(), 0.7));
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("jev", result.IntentSource);
        Assert.Equal("medium", result.IntentConfidenceBand);
    }

    [Fact]
    public async Task JevMode_LowConfidence_RoutesToUnclearWithoutFallback()
    {
        var llm = Classifier(LlmDecision("kb_question"));
        var jev = Classifier(JevDecision(KbLeaning(), 0.4));
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        Assert.Equal("unclear", result.Intent);
        Assert.Equal("low-confidence", result.IntentRule);
        Assert.Equal("jev", result.IntentSource);
        Assert.Null(result.IntentConfidenceBand);
        // Policy-driven unclear is a Jev answer, not a failure, so the LLM is not called.
        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_WhenJevThrows_FallsBackToLlmWithWarningAndNoQueryText()
    {
        var llm = Classifier(LlmDecision("code_issue", tokens: 9));
        var jev = FailingClassifier(new HttpRequestException("connection refused"));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, logger);

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        Assert.Equal("code_issue", result.Intent);
        Assert.Equal("llm-fallback", result.IntentSource);
        Assert.Null(result.IntentConfidenceBand);
        Assert.Equal(9, result.TokensByAgent["Triage"]);
        Assert.True(HasWarning(logger, "TriageJevFallback"));
        Assert.DoesNotContain(logger.Entries, e =>
            e.Message.Contains(Query, StringComparison.Ordinal)
            || (e.Exception?.Message.Contains(Query, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task JevMode_WhenJevRunsPastTimeout_FallsBackPromptly()
    {
        var llm = Classifier(LlmDecision("kb_question"));
        var jev = SlowCooperativeClassifier(TimeSpan.FromSeconds(5), JevDecision(KbLeaning(), 0.92));
        var options = new TriageOptions { Classifier = TriageClassifierMode.Jev, JevTimeout = TimeSpan.FromMilliseconds(50) };
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, options, logger);
        var stopwatch = Stopwatch.StartNew();

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Fallback took {stopwatch.ElapsedMilliseconds} ms.");
        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("llm-fallback", result.IntentSource);
        Assert.True(HasWarning(logger, "TriageJevFallback"));
    }

    [Fact]
    public async Task JevMode_UncooperativeJev_IsAbandonedAtBudget()
    {
        // The Jev classifier ignores its token, so only the agent's own budget can end the wait.
        var llm = Classifier(LlmDecision("screenshot_error"));
        var jev = SlowUncooperativeClassifier(TimeSpan.FromSeconds(5), JevDecision(KbLeaning(), 0.92));
        var options = new TriageOptions { Classifier = TriageClassifierMode.Jev, JevTimeout = TimeSpan.FromMilliseconds(50) };
        var agent = new TriageAgent(llm.Object, jev.Object, options, new ListLogger<TriageAgent>());
        var stopwatch = Stopwatch.StartNew();

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Wait took {stopwatch.ElapsedMilliseconds} ms.");
        Assert.Equal("screenshot_error", result.Intent);
        Assert.Equal("llm-fallback", result.IntentSource);
    }

    [Fact]
    public async Task JevMode_CallerCancellation_PropagatesWithoutFallback()
    {
        var llm = Classifier(LlmDecision("kb_question"));
        var jev = SlowCooperativeClassifier(TimeSpan.FromSeconds(5), JevDecision(KbLeaning(), 0.92));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query }, cts.Token));

        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(HasWarning(logger, "TriageJevFallback"));
    }

    [Fact]
    public async Task ShadowMode_ActsOnLlm_EvenWhenJevDisagree_AndLogsComparison()
    {
        var llm = Classifier(LlmDecision("kb_question", tokens: 15));
        var jev = Classifier(JevDecision(CodeIssueLeaning(), 0.9, tokens: 70));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Shadow }, logger);

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("llm", result.IntentSource);
        Assert.Null(result.IntentConfidenceBand);
        Assert.Equal(15, result.TokensByAgent["Triage"]);
        Assert.Equal(70, result.TokensByAgent["Triage.Shadow"]);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.StartsWith("TriageShadow", StringComparison.Ordinal)
            && e.Message.Contains("agree=False", StringComparison.Ordinal)
            && e.Message.Contains("jevIntent=code_issue", StringComparison.Ordinal)
            && e.Message.Contains("jevFailed=False", StringComparison.Ordinal));
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShadowMode_WhenJevThrows_IsSwallowedAndLoggedAsFailed()
    {
        var llm = Classifier(LlmDecision("code_issue", tokens: 9));
        var jev = FailingClassifier(new HttpRequestException("jev down"));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Shadow }, logger);

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        Assert.Equal("code_issue", result.Intent);
        Assert.Equal("llm", result.IntentSource);
        Assert.Equal(9, result.TokensByAgent["Triage"]);
        Assert.False(result.TokensByAgent.ContainsKey("Triage.Shadow"));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.StartsWith("TriageShadow", StringComparison.Ordinal)
            && e.Message.Contains("jevFailed=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShadowMode_SlowJev_DoesNotDelayPastBudget()
    {
        var llm = Classifier(LlmDecision("kb_question"));
        var jev = SlowUncooperativeClassifier(TimeSpan.FromSeconds(5), JevDecision(KbLeaning(), 0.92));
        var options = new TriageOptions { Classifier = TriageClassifierMode.Shadow, JevTimeout = TimeSpan.FromMilliseconds(50) };
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, options, logger);
        var stopwatch = Stopwatch.StartNew();

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = Query });

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Shadow waited {stopwatch.ElapsedMilliseconds} ms for Jev.");
        Assert.Equal("kb_question", result.Intent);
        Assert.Contains(logger.Entries, e =>
            e.Message.StartsWith("TriageShadow", StringComparison.Ordinal)
            && e.Message.Contains("jevFailed=True", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TriageClassifierMode.Jev)]
    [InlineData(TriageClassifierMode.Shadow)]
    public void Constructor_JevOrShadowWithoutJevClassifier_ThrowsArgumentException(TriageClassifierMode mode)
    {
        var llm = Classifier(LlmDecision("kb_question"));

        Assert.Throws<ArgumentException>(() =>
            new TriageAgent(llm.Object, null, new TriageOptions { Classifier = mode }, new ListLogger<TriageAgent>()));
    }

    [Fact]
    public async Task JevMode_GreetingWithSupportQuestion_RoutesByPolicyRule()
    {
        // The greeting leads, but the question is real. Rule 1 takes the best non-greeting label (kb_question). Its margin is
        // checked against the next label still in play (code_question at 0.05), so 0.30 - 0.05 = 0.25 clears MinMargin (0.15).
        var scores = new Dictionary<string, double>
        {
            [TriageLabels.Greeting] = 0.6,
            [TriageLabels.KbQuestion] = 0.3,
            [TriageLabels.CodeQuestion] = 0.05,
            [TriageLabels.CodeIssue] = 0.03,
            [TriageLabels.ScreenshotError] = 0.02,
        };
        var signals = new Dictionary<string, double>
        {
            [TriageSignals.HasSupportQuestion] = 0.9,
            [TriageSignals.RefersToImage] = 0.05,
        };
        var llm = Classifier(LlmDecision("greeting"));
        var jev = Classifier(JevDecision(scores, 0.8, signals: signals));
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = "thanks! how do I reset my password?" });

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("greeting-has-question", result.IntentRule);
        Assert.Equal("jev", result.IntentSource);
        Assert.Equal("medium", result.IntentConfidenceBand);
    }
}
