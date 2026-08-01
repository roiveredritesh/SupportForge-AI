using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class VisionAnalyzerAgentTests
{
    [Fact]
    public async Task RunAsync_SetsVisionFindings_WhenScreenshotPresent()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SupportsVision).Returns(true);
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("NullReferenceException at CheckoutController.cs:42");

        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object), new ListLogger<VisionAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail", ScreenshotBase64 = "base64data" };

        var result = await agent.RunAsync(context);

        Assert.Equal("NullReferenceException at CheckoutController.cs:42", result.VisionFindings);
    }

    [Fact]
    public async Task RunAsync_SkipsAnalysis_WhenNoScreenshot()
    {
        var llm = new Mock<ILlmClient>(MockBehavior.Strict);
        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object), new ListLogger<VisionAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail" };

        var result = await agent.RunAsync(context);

        Assert.Null(result.VisionFindings);
        llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_OnRetry_RequestsDetailedAnalysis()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SupportsVision).Returns(true);
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.Is<string>(p => p.Contains("in detail")), It.IsAny<CancellationToken>()))
            .ReturnsAsync("detailed findings");

        var tool = new VisionAnalysisTool(llm.Object);
        var agent = new VisionAnalyzerAgent(tool, new ListLogger<VisionAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "base64data" };
        context.VisionVerification.Attempts = 1;

        var result = await agent.RunAsync(context);

        Assert.Equal("detailed findings", result.VisionFindings);
        Assert.Equal(2, result.VisionVerification.Attempts);
    }

    [Fact]
    public async Task RunAsync_SkipsAnalysisWithReason_WhenModelDoesNotSupportVision()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SupportsVision).Returns(false);

        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object), new ListLogger<VisionAnalyzerAgent>());
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail", ScreenshotBase64 = "base64data" };

        var result = await agent.RunAsync(context);

        Assert.Equal("Vision analysis unavailable: the configured chat model does not support vision.", result.VisionFindings);
        llm.Verify(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_LogsStartAndCompletion()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SupportsVision).Returns(true);
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("NullReferenceException at CheckoutController.cs:42");

        var logger = new ListLogger<VisionAnalyzerAgent>();
        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object), logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail", ScreenshotBase64 = "base64data" };

        await agent.RunAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starting") && e.Message.Contains("VisionAnalyzer"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("completed"));
    }

    [Fact]
    public async Task RunAsync_WhenAnalysisThrows_LogsFailureAndPropagates()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.SupportsVision).Returns(true);
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ThrowsAsync(new InvalidOperationException("vision api down"));

        var logger = new ListLogger<VisionAnalyzerAgent>();
        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object), logger);
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail", ScreenshotBase64 = "base64data" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(context));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
