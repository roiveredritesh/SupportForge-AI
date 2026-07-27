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
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("NullReferenceException at CheckoutController.cs:42");

        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object));
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail", ScreenshotBase64 = "base64data" };

        var result = await agent.RunAsync(context);

        Assert.Equal("NullReferenceException at CheckoutController.cs:42", result.VisionFindings);
    }

    [Fact]
    public async Task RunAsync_SkipsAnalysis_WhenNoScreenshot()
    {
        var llm = new Mock<ILlmClient>(MockBehavior.Strict);
        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object));
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail" };

        var result = await agent.RunAsync(context);

        Assert.Null(result.VisionFindings);
        llm.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_OnRetry_RequestsDetailedAnalysis()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.Is<string>(p => p.Contains("in detail")), It.IsAny<CancellationToken>()))
            .ReturnsAsync("detailed findings");

        var tool = new VisionAnalysisTool(llm.Object);
        var agent = new VisionAnalyzerAgent(tool);
        var context = new AgentContext { ProjectId = "p", Query = "q", ScreenshotBase64 = "base64data" };
        context.VisionVerification.Attempts = 1;

        var result = await agent.RunAsync(context);

        Assert.Equal("detailed findings", result.VisionFindings);
        Assert.Equal(2, result.VisionVerification.Attempts);
    }
}
