using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CodeAnalyzerVerifierTests
{
    [Fact]
    public async Task RunAsync_IntentNotCodeRelated_StaysNotRun()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new CodeAnalyzerVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.NotRun, result.CodeVerification.Status);
    }

    [Fact]
    public async Task RunAsync_CodeIssueWithNoSnippets_FailsAndRetriesWhenUnderAttemptLimit()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new CodeAnalyzerVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.CodeVerification.Status);
    }

    [Fact]
    public async Task RunAsync_MultipleSnippets_PassesWithoutCallingLlm()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new CodeAnalyzerVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_question" };
        context.CodeSnippets.Add("snippet 1");
        context.CodeSnippets.Add("snippet 2");

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.CodeVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_SingleSnippet_JudgeYes_Passes()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("yes");
        llm.SetupGet(l => l.LastTotalTokens).Returns(100);
        var verifier = new CodeAnalyzerVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "how to optimize", Intent = "code_question" };
        context.CodeSnippets.Add("public class OptimizedCode { ... }");

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.CodeVerification.Status);
        Assert.Equal(100, result.TotalTokensUsed);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SingleSnippet_JudgeNo_FailsFinal()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no");
        var verifier = new CodeAnalyzerVerifier(llm.Object);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.Add("unrelated snippet");
        context.CodeVerification.Attempts = 2; // at limit, so judged "no" should be FailedFinal

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.CodeVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
