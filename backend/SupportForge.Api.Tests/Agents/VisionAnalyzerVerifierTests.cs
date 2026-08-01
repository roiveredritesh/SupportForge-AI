using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class VisionAnalyzerVerifierTests
{
    [Fact]
    public async Task RunAsync_NoScreenshot_StaysNotRun()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new VisionAnalyzerVerifier(llm.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q" };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.NotRun, result.VisionVerification.Status);
    }

    [Fact]
    public async Task RunAsync_EmptyFindings_FailsAndRetriesWhenUnderAttemptLimit()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new VisionAnalyzerVerifier(llm.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "data", VisionFindings = "" };
        context.VisionVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.VisionVerification.Status);
    }

    [Fact]
    public async Task RunAsync_SubstantialFindings_PassesWithoutCallingLlm()
    {
        var llm = new Mock<ILlmClient>();
        var verifier = new VisionAnalyzerVerifier(llm.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", ScreenshotBase64 = "data",
            VisionFindings = "The screenshot shows a 500 Internal Server Error dialog with stack trace visible.",
        };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.VisionVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_BorderlineFindings_JudgeYes_Passes()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("yes");
        llm.As<ILlmChatClient>().SetupGet(l => l.LastTotalTokens).Returns(50);
        var verifier = new VisionAnalyzerVerifier(llm.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "data", VisionFindings = "Error" };

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.Passed, result.VisionVerification.Status);
        Assert.Equal(50, result.TotalTokensUsed);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_BorderlineFindings_JudgeNo_FailsFinal()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no");
        var verifier = new VisionAnalyzerVerifier(llm.Object, new ListLogger<VisionAnalyzerVerifier>());
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "data", VisionFindings = "N/A" };
        context.VisionVerification.Attempts = 2; // at limit, so judged "no" should be FailedFinal

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedFinal, result.VisionVerification.Status);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var llm = new Mock<ILlmClient>();
        var logger = new ListLogger<VisionAnalyzerVerifier>();
        var verifier = new VisionAnalyzerVerifier(llm.Object, logger);
        var context = new AgentContext
        {
            ProjectId = "p", Query = "q", ScreenshotBase64 = "data",
            VisionFindings = "The screenshot shows a 500 Internal Server Error dialog with stack trace visible.",
        };

        await verifier.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("VisionAnalyzerVerifier"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed") && e.Message.Contains("Passed"));
    }

    [Fact]
    public async Task RunAsync_OnRetry_LogsAttemptNumberAndReason()
    {
        var llm = new Mock<ILlmClient>();
        var logger = new ListLogger<VisionAnalyzerVerifier>();
        var verifier = new VisionAnalyzerVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "data", VisionFindings = "" };
        context.VisionVerification.Attempts = 1;

        var result = await verifier.RunAsync(context);

        Assert.Equal(VerificationStatus.FailedRetrying, result.VisionVerification.Status);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("retrying")
            && e.Message.Contains("attempt=1")
            && e.Message.Contains("Vision analysis returned no findings"));
    }

    [Fact]
    public async Task RunAsync_WhenJudgeThrows_LogsFailureAndPropagates()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("judge llm down"));
        var logger = new ListLogger<VisionAnalyzerVerifier>();
        var verifier = new VisionAnalyzerVerifier(llm.Object, logger);
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "data", VisionFindings = "Error" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
