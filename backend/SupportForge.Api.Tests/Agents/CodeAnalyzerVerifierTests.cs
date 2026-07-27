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
}
