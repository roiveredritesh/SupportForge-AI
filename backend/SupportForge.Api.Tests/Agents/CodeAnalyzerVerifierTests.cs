using Microsoft.Extensions.Logging;
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
        var verifier = new CodeAnalyzerVerifier(llm.Object, new ListLogger<CodeAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.NotRun, result.CodeVerification.Status);
    }

    [Fact]
    public async Task RunAsync_CodeIssueWithNoSnippets_FailsAndRetriesWhenUnderAttemptLimit()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new CodeAnalyzerVerifier(llm.Object, new ListLogger<CodeAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.CodeVerification.Status);
    }

    [Fact]
    public async Task RunAsync_MultipleSnippets_PassesWithoutCallingLlm()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new CodeAnalyzerVerifier(llm.Object, new ListLogger<CodeAnalyzerVerifier>());
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
        llm.As<ILlmChatClient>().SetupGet(l => l.LastTotalTokens).Returns(100);
        var verifier = new CodeAnalyzerVerifier(llm.Object, new ListLogger<CodeAnalyzerVerifier>());
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
        var verifier = new CodeAnalyzerVerifier(llm.Object, new ListLogger<CodeAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.Add("unrelated snippet");
        context.CodeVerification.Attempts = 2; // at limit, so judged "no" should be FailedFinal

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.CodeVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var llm = new Mock<ILlmClient>();
        var logger = new ListLogger<CodeAnalyzerVerifier>();
        var verifier = new CodeAnalyzerVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_question" };
        context.CodeSnippets.Add("snippet 1");
        context.CodeSnippets.Add("snippet 2");

        await verifier.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("CodeAnalyzerVerifier"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed") && e.Message.Contains("Passed"));
    }

    [Fact]
    public async Task RunAsync_OnRetry_LogsAttemptNumberAndReason()
    {
        var llm = new Mock<ILlmClient>();
        var logger = new ListLogger<CodeAnalyzerVerifier>();
        var verifier = new CodeAnalyzerVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };
        context.CodeVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.CodeVerification.Status);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("retrying")
            && e.Message.Contains("attempt=1")
            && e.Message.Contains("No code snippets were retrieved"));
    }

    [Fact]
    public async Task RunAsync_WhenJudgeThrows_LogsFailureAndPropagates()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("judge llm down"));
        var logger = new ListLogger<CodeAnalyzerVerifier>();
        var verifier = new CodeAnalyzerVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_question" };
        context.CodeSnippets.Add("one weak snippet");

        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
