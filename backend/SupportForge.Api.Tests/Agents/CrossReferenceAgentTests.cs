using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CrossReferenceAgentTests
{
    [Theory]
    [InlineData("kb_question")]
    [InlineData("screenshot_error")]
    [InlineData("unclear")]
    public async Task RunAsync_IntentDoesNotUseCode_SkipsExtraction(string intent)
    {
        var llm = new Mock<ILlmClient>();
        var agent = new CrossReferenceAgent(llm.Object, NullLogger<CrossReferenceAgent>.Instance);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = intent };
        context.KbSnippets.Add("the v2/auth endpoint handles login");

        var result = await agent.RunAsync(context);

        Assert.Null(result.CodeQueryAugmentation);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoKbSnippets_SkipsExtraction()
    {
        var llm = new Mock<ILlmClient>();
        var agent = new CrossReferenceAgent(llm.Object, NullLogger<CrossReferenceAgent>.Instance);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_issue" };

        var result = await agent.RunAsync(context);

        Assert.Null(result.CodeQueryAugmentation);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_KbFindsConcreteTerms_SetsCodeQueryAugmentation()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/api/v2/auth");
        var agent = new CrossReferenceAgent(llm.Object, NullLogger<CrossReferenceAgent>.Instance);
        var context = new AgentContext { ProjectId = "p", Query = "why do I get a 500", Intent = "code_issue" };
        context.KbSnippets.Add("the /api/v2/auth endpoint handles login");

        var result = await agent.RunAsync(context);

        Assert.Equal("/api/v2/auth", result.CodeQueryAugmentation);
        llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_NothingConcreteToExtract_LeavesAugmentationNull()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("none");
        var agent = new CrossReferenceAgent(llm.Object, NullLogger<CrossReferenceAgent>.Instance);
        var context = new AgentContext { ProjectId = "p", Query = "q", Intent = "code_question" };
        context.KbSnippets.Add("a general overview paragraph with no specifics");

        var result = await agent.RunAsync(context);

        Assert.Null(result.CodeQueryAugmentation);
    }
}
