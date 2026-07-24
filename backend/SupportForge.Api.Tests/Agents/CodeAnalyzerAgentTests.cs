using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CodeAnalyzerAgentTests
{
    [Fact]
    public async Task RunAsync_SkipsSearch_WhenIntentIsNotCodeIssue()
    {
        var llm = new Mock<ILlmClient>();
        var vectorStore = new Mock<IVectorStoreService>(MockBehavior.Strict);
        var agent = new CodeAnalyzerAgent(new CodeSearchTool(llm.Object, vectorStore.Object));

        var context = new AgentContext { ProjectId = "proj1", Query = "what is my account balance", Intent = "kb_question" };
        var result = await agent.RunAsync(context);

        Assert.Empty(result.CodeSnippets);
        vectorStore.VerifyNoOtherCalls();
    }
}
