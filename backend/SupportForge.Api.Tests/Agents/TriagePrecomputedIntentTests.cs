using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U17 / KTD 13: when the guard's combined Jev request already produced the triage answer, TriageAgent acts on it in Jev mode with no
// second classifier call. Llm and Shadow modes ignore it. The policy still runs on the precomputed decision.
public class TriagePrecomputedIntentTests
{
    private const string Query = "my invoice export 4417 never finishes";

    private static IntentDecision LlmDecision(string label, int tokens = 7) =>
        new(new Dictionary<string, double> { [label] = 1.0 }, 1.0, new Dictionary<string, double>(), tokens, "llm");

    private static IntentDecision JevDecision(IReadOnlyDictionary<string, double> scores, double confidence, int tokens = 42) =>
        new(scores, confidence, new Dictionary<string, double>(), tokens, "jev");

    private static IReadOnlyDictionary<string, double> KbLeaning() => new Dictionary<string, double>
    {
        [TriageLabels.KbQuestion] = 0.9,
        [TriageLabels.CodeQuestion] = 0.05,
        [TriageLabels.Greeting] = 0.05,
    };

    private static Mock<IIntentClassifier> Classifier(IntentDecision decision)
    {
        var classifier = new Mock<IIntentClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(decision);
        return classifier;
    }

    private static bool HasInfo(ListLogger<TriageAgent> logger, string prefix) =>
        logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.StartsWith(prefix, StringComparison.Ordinal));

    private static AgentContext ContextWith(IntentDecision? precomputed) =>
        new() { ProjectId = "proj1", Query = Query, PrecomputedIntent = precomputed };

    [Fact]
    public async Task JevMode_PrecomputedIntent_IsActedOnWithoutAnyClassifierCall()
    {
        var llm = Classifier(LlmDecision("code_issue"));
        var jev = Classifier(JevDecision(KbLeaning(), 0.92));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, logger);

        var result = await agent.RunAsync(ContextWith(JevDecision(KbLeaning(), 0.92, tokens: 42)));

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("jev", result.IntentSource);
        Assert.Equal("top", result.IntentRule);
        Assert.Equal(0.92, result.IntentConfidence);
        Assert.Equal("high", result.IntentConfidenceBand);
        Assert.Equal(42, result.TokensByAgent["Triage"]);
        Assert.True(HasInfo(logger, "TriagePrecomputed"));
        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_PrecomputedIntent_StillGoesThroughThePolicy()
    {
        // A low-confidence precomputed answer is "unclear" by the same rule as a Jev answer from the classifier.
        var jev = Classifier(JevDecision(KbLeaning(), 0.4));
        var agent = new TriageAgent(Classifier(LlmDecision("kb_question")).Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(ContextWith(JevDecision(KbLeaning(), 0.4)));

        Assert.Equal("unclear", result.Intent);
        Assert.Equal("low-confidence", result.IntentRule);
        Assert.Equal("jev", result.IntentSource);
        Assert.Null(result.IntentConfidenceBand);
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevMode_WithoutPrecomputedIntent_StillCallsTheClassifier()
    {
        // The normal path is unchanged when the guard did not supply a precomputed answer.
        var jev = Classifier(JevDecision(KbLeaning(), 0.92));
        var agent = new TriageAgent(Classifier(LlmDecision("code_issue")).Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Jev }, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(ContextWith(null));

        Assert.Equal("kb_question", result.Intent);
        Assert.Equal("jev", result.IntentSource);
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LlmMode_PrecomputedIntent_IsIgnored()
    {
        var llm = Classifier(LlmDecision("code_issue", tokens: 7));
        var jev = Classifier(JevDecision(KbLeaning(), 0.92));
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Llm }, new ListLogger<TriageAgent>());

        var result = await agent.RunAsync(ContextWith(JevDecision(KbLeaning(), 0.92, tokens: 42)));

        Assert.Equal("code_issue", result.Intent);
        Assert.Equal("llm", result.IntentSource);
        Assert.Equal(7, result.TokensByAgent["Triage"]);
        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShadowMode_PrecomputedIntent_IsIgnored_AndLlmStillActs()
    {
        // Shadow keeps its own contract: the LLM is acted on, and Jev is called for the comparison log. The precomputed answer is not used.
        var llm = Classifier(LlmDecision("code_issue", tokens: 7));
        var jev = Classifier(JevDecision(KbLeaning(), 0.92));
        var logger = new ListLogger<TriageAgent>();
        var agent = new TriageAgent(llm.Object, jev.Object, new TriageOptions { Classifier = TriageClassifierMode.Shadow }, logger);

        var result = await agent.RunAsync(ContextWith(JevDecision(KbLeaning(), 0.92, tokens: 42)));

        Assert.Equal("code_issue", result.Intent);
        Assert.Equal("llm", result.IntentSource);
        Assert.Equal(7, result.TokensByAgent["Triage"]);
        Assert.False(HasInfo(logger, "TriagePrecomputed"));
        Assert.True(HasInfo(logger, "TriageShadow"));
        llm.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        jev.Verify(c => c.ClassifyAsync(It.IsAny<AgentContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
