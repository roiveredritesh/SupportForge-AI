using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Triage;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U2 (JEV intent classification plan): LlmIntentClassifier must reproduce TriageAgent's pre-seam LLM behaviour.
public class LlmIntentClassifierTests
{
    private static AgentContext NewContext(string query = "how does checkout work?") =>
        new() { ProjectId = "proj1", Query = query };

    [Fact]
    public async Task ClassifyAsync_MapsDecoratedLabel_ToCertainDecision()
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync("**code_question**");
        var classifier = new LlmIntentClassifier(llm.Object);

        var decision = await classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None);

        var score = Assert.Single(decision.Scores);
        Assert.Equal("code_question", score.Key);
        Assert.Equal(1.0, score.Value);
        Assert.Equal(1.0, decision.Confidence);
        Assert.Empty(decision.Signals);
        Assert.Equal("llm", decision.Source);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownLabel_MapsToUnclear()
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync("bug_report");
        var classifier = new LlmIntentClassifier(llm.Object);

        var decision = await classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None);

        var score = Assert.Single(decision.Scores);
        Assert.Equal("unclear", score.Key);
        Assert.Equal(1.0, score.Value);
    }

    [Fact]
    public async Task ClassifyAsync_ReportsLlmTotalTokens()
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync("greeting");
        llm.SetupGet(l => l.LastTotalTokens).Returns(42);
        var classifier = new LlmIntentClassifier(llm.Object);

        var decision = await classifier.ClassifyAsync(NewContext("hi there"), TriageLabels.All, CancellationToken.None);

        Assert.Equal(42, decision.TokensUsed);
    }

    [Fact]
    public async Task ClassifyAsync_SendsContextQueryAsUserPrompt()
    {
        string? userPrompt = null;
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback((string systemText, string userText, CancellationToken token) => userPrompt = userText)
           .ReturnsAsync("kb_question");
        var classifier = new LlmIntentClassifier(llm.Object);

        await classifier.ClassifyAsync(NewContext("where do I reset my password?"), TriageLabels.All, CancellationToken.None);

        Assert.Equal("where do I reset my password?", userPrompt);
    }

    [Fact]
    public async Task ClassifyAsync_WhenLlmThrows_PropagatesException()
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("llm down"));
        var classifier = new LlmIntentClassifier(llm.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => classifier.ClassifyAsync(NewContext(), TriageLabels.All, CancellationToken.None));
    }

    [Fact]
    public async Task ClassifyAsync_SystemPrompt_StillOffersUnclearAndGreetingLabels()
    {
        string? systemPrompt = null;
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback((string systemText, string userText, CancellationToken token) => systemPrompt = systemText)
           .ReturnsAsync("unclear");
        var classifier = new LlmIntentClassifier(llm.Object);

        await classifier.ClassifyAsync(NewContext("it broke"), TriageLabels.All, CancellationToken.None);

        Assert.Contains("\"unclear\"", systemPrompt);
        Assert.Contains("\"greeting\"", systemPrompt);
    }
}
