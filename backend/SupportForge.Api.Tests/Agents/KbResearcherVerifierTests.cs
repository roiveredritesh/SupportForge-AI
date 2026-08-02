using Microsoft.Extensions.Logging;
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
        var verifier = new KbResearcherVerifier(llm.Object, new ListLogger<KbResearcherVerifier>());
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
        var verifier = new KbResearcherVerifier(llm.Object, new ListLogger<KbResearcherVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbVerification.Attempts = 2;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.KbVerification.Status);
    }

    [Theory]
    [InlineData("kb_question")]
    [InlineData("code_issue")]
    [InlineData("code_question")]
    public async Task RunAsync_MultipleSnippets_EscalatesToLlmJudgeOnTopSnippet(string intent)
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("yes");
        var verifier = new KbResearcherVerifier(llm.Object, new ListLogger<KbResearcherVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = intent };
        context.KbSnippets.Add("snippet 1");
        context.KbSnippets.Add("snippet 2");

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MultipleSnippets_AllIrrelevant_FailsInsteadOfAutoPassing()
    {
        // Regression test: nearest-neighbor vector search always returns topK results even when
        // nothing in the KB is actually relevant. Multiple irrelevant snippets must not auto-pass.
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no");
        var verifier = new KbResearcherVerifier(llm.Object, new ListLogger<KbResearcherVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_question" };
        context.KbSnippets.Add("unrelated snippet 1");
        context.KbSnippets.Add("unrelated snippet 2");
        context.KbVerification.Attempts = 2; // at limit, so a judged "no" should be FailedFinal

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SingleSnippet_EscalatesToLlmJudge()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no");
        var verifier = new KbResearcherVerifier(llm.Object, new ListLogger<KbResearcherVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.Add("one weak snippet");
        context.KbVerification.Attempts = 2; // at limit, so a judged "no" should be FailedFinal not FailedRetrying

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("screenshot_error")]
    [InlineData("unclear")]
    public async Task RunAsync_UnrelatedIntent_LeavesVerificationNotRun(string intent)
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new KbResearcherVerifier(llm.Object, new ListLogger<KbResearcherVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = intent };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.NotRun, result.KbVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("yes");
        var logger = new ListLogger<KbResearcherVerifier>();
        var verifier = new KbResearcherVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.Add("snippet 1");
        context.KbSnippets.Add("snippet 2");

        await verifier.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("KbResearcherVerifier"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed") && e.Message.Contains("Passed"));
    }

    [Fact]
    public async Task RunAsync_OnRetry_LogsAttemptNumberAndReason()
    {
        var llm = new Mock<ILlmClient>();
        var logger = new ListLogger<KbResearcherVerifier>();
        var verifier = new KbResearcherVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.KbVerification.Status);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("retrying")
            && e.Message.Contains("attempt=1")
            && e.Message.Contains("No KB snippets were retrieved."));
    }

    [Fact]
    public async Task RunAsync_WhenJudgeThrows_LogsFailureAndPropagates()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("judge llm down"));
        var logger = new ListLogger<KbResearcherVerifier>();
        var verifier = new KbResearcherVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "kb_question" };
        context.KbSnippets.Add("one weak snippet");

        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
