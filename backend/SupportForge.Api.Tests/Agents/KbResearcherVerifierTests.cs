using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class KbResearcherVerifierTests
{
    [Fact]
    public async Task RunAsync_NoSnippets_FailsAndRetriesWhenUnderAttemptLimit()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new KbResearcherVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoSnippets_FailsFinalWhenAttemptLimitReached()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new KbResearcherVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbVerification.Attempts = 2;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.KbVerification.Status);
    }

    [Fact]
    public async Task RunAsync_MultipleSnippets_PassesWithoutCallingLlm()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new KbResearcherVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.Add("snippet 1");
        context.KbSnippets.Add("snippet 2");

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_SingleSnippet_EscalatesToLlmJudge()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no");
        var verifier = new KbResearcherVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.Add("one weak snippet");
        context.KbVerification.Attempts = 2; // at limit, so a judged "no" should be FailedFinal not FailedRetrying

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("code_question")]
    [InlineData("screenshot_error")]
    [InlineData("unclear")]
    public async Task RunAsync_UnrelatedIntent_LeavesVerificationNotRun(string intent)
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new KbResearcherVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = intent };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.NotRun, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
