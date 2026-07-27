using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
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

    [Fact]
    public async Task RunAsync_SearchesCode_WhenIntentIsCodeQuestion()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore
            .Setup(v => v.QueryAsync("proj1-code", It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(new List<VectorQueryResult>
            {
                new("id1", "public class CodeAnalyzerAgent { ... }", 0.9f, new Dictionary<string, string> { ["file"] = "CodeAnalyzerAgent.cs" }),
            });

        var agent = new CodeAnalyzerAgent(new CodeSearchTool(llm.Object, vectorStore.Object));
        var context = new AgentContext { ProjectId = "proj1", Query = "how does CodeAnalyzerAgent work?", Intent = "code_question" };

        var result = await agent.RunAsync(context);

        Assert.Single(result.CodeSnippets);
        Assert.Contains(result.Sources, s => s.Label == "Code: CodeAnalyzerAgent.cs");
    }

    [Fact]
    public async Task RunAsync_OnRetry_ClearsPreviousSnippetsAndWidensTopK()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-code", It.IsAny<float[]>(), 10, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-2", "retry result", 0.2f, new Dictionary<string, string> { ["file"] = "Retry.cs" }) });

        var tool = new CodeSearchTool(llm.Object, vectorStore.Object);
        var agent = new CodeAnalyzerAgent(tool);
        var context = new AgentContext { ProjectId = "proj1", Query = "q", Intent = "code_issue" };
        context.CodeSnippets.Add("stale snippet");
        context.CodeVerification.Attempts = 1;

        var result = await agent.RunAsync(context);

        Assert.Single(result.CodeSnippets);
        Assert.Equal("retry result", result.CodeSnippets[0]);
        Assert.Equal(2, result.CodeVerification.Attempts);
    }
}

