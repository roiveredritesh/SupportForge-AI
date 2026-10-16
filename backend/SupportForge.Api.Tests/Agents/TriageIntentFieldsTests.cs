using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U3 (JEV intent classification plan): TriageAgent writes the richer intent fields onto AgentContext, and
// AgentContext derives RunnerUpIntent from them. Label-level behaviour is covered by TriageAgentTests.cs.
public class TriageIntentFieldsTests
{
    private static Mock<IIntentClassifier> ClassifierReturning(IntentDecision decision)
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(decision);
        return classifier;
    }

    private static IntentDecision Decide(IReadOnlyDictionary<string, double> scores, double confidence = 1.0, int tokens = 0) =>
        new(scores, confidence, new Dictionary<string, double>(), tokens, "jev");

    [Fact]
    public async Task RunAsync_WithLlmConstructor_WritesCertainIntentFields()
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync("code_issue");
        llm.SetupGet(l => l.LastTotalTokens).Returns(42);

        var agent = new TriageAgent(llm.Object, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error on checkout" };

        var result = await agent.RunAsync(context);

        Assert.Equal("code_issue", result.Intent);
        Assert.Equal(1.0, result.IntentConfidence);
        var score = Assert.Single(result.IntentScores);
        Assert.Equal("code_issue", score.Key);
        Assert.Equal(1.0, score.Value);
        Assert.Equal("llm", result.IntentSource);
        Assert.Null(result.RunnerUpIntent);
        Assert.Equal(42, result.TokensByAgent["Triage"]);
    }

    [Fact]
    public async Task RunAsync_WithClassifier_WritesScoresConfidenceSourceAndRunnerUp()
    {
        var classifier = ClassifierReturning(new IntentDecision(
            new Dictionary<string, double> { ["kb_question"] = 0.55, ["code_question"] = 0.38, ["greeting"] = 0.07 },
            0.6,
            new Dictionary<string, double> { ["has_stack_trace"] = 1.0 },
            17,
            "jev"));
        var agent = new TriageAgent(classifier.Object, new ListLogger<TriageAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "how do I export invoices?" };

        var result = await agent.RunAsync(context);

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal(0.6, result.IntentConfidence);
        Assert.Equal("jev", result.IntentSource);
        Assert.Equal(1.0, result.IntentSignals["has_stack_trace"]);
        Assert.Equal(("code_question", 0.38), result.RunnerUpIntent);
        Assert.Equal(17, result.TokensByAgent["Triage"]);
        classifier.Verify(c => c.ClassifyAsync(context, TriageLabels.All, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_TopLabelOutsideKnownSet_MapsToUnclear()
    {
        var classifier = ClassifierReturning(Decide(new Dictionary<string, double> { ["bogus_label"] = 0.9 }, 0.9));
        var agent = new TriageAgent(classifier.Object, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = "it broke" });

        Assert.Equal("unclear", result.Intent);
        Assert.Equal(0.9, result.IntentScores["bogus_label"]);
    }

    [Fact]
    public async Task RunAsync_EmptyScores_MapsToUnclear()
    {
        var classifier = ClassifierReturning(Decide(new Dictionary<string, double>(), 0.0));
        var agent = new TriageAgent(classifier.Object, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = "it broke" });

        Assert.Equal("unclear", result.Intent);
        Assert.Empty(result.IntentScores);
    }

    [Fact]
    public async Task RunAsync_TiedScores_BreakByTriageLabelsOrder()
    {
        // code_issue is inserted first on purpose: the winner must come from TriageLabels.All order, not insertion order.
        var classifier = ClassifierReturning(Decide(
            new Dictionary<string, double> { ["code_issue"] = 0.5, ["kb_question"] = 0.5 },
            0.5));
        var agent = new TriageAgent(classifier.Object, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(new AgentContext { ProjectId = "proj1", Query = "hmm" });

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal(("code_issue", 0.5), result.RunnerUpIntent);
    }

    [Fact]
    public async Task RunAsync_WhenClassifierThrows_LogsFailureAndPropagates()
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("classifier down"));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(classifier.Object, logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "it broke" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
        Assert.Equal(string.Empty, context.Intent);
    }

    [Fact]
    public void RunnerUpIntent_IgnoresZeroScoredLabels()
    {
        var context = new AgentContext
        {
            ProjectId = "proj1",
            Query = "hi",
            Intent = "kb_question",
            IntentScores = new Dictionary<string, double> { ["kb_question"] = 0.9, ["code_question"] = 0.0, ["greeting"] = 0.0 },
        };

        Assert.Null(context.RunnerUpIntent);
    }

    [Fact]
    public void SecondaryIntentDefaults_AreOffWithThreshold035()
    {
        var context = new AgentContext { ProjectId = "proj1", Query = "hi" };

        Assert.False(context.SecondaryIntentEnabled);
        Assert.Equal(0.35, context.SecondaryMinProbability);
    }
}
