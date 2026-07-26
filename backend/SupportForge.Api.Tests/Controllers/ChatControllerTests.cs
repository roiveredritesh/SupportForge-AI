using Microsoft.AspNetCore.Mvc;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Api.Controllers;
using SupportForge.Api.Contracts;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ChatControllerTests
{
    private static ChatController MakeController(CoordinatorPipeline pipeline, ILlmClient llm, ITokenUsageRepository tokenUsage)
    {
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(new List<VectorQueryResult>());

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());

        return new ChatController(
            pipeline,
            new TriageAgent(llm),
            new KbResearcherAgent(new KbSearchTool(llm, vectorStore.Object)),
            new CodeAnalyzerAgent(new CodeSearchTool(llm, vectorStore.Object)),
            new VisionAnalyzerAgent(new VisionAnalysisTool(llm)),
            llm,
            tokenUsage,
            conversations.Object,
            messages.Object);
    }

    [Fact]
    public async Task Query_ReturnsDraftAndConfidence_FromPipeline()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm), new NoOpAgent("KbResearcher"), new NoOpAgent("CodeAnalyzer"), new NoOpAgent("VisionAnalyzer"), new DrafterAgent(openAiLlm));
        var tokenUsage = new Mock<ITokenUsageRepository>();
        var controller = MakeController(pipeline, openAiLlm, tokenUsage.Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "Getting a 500 error" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        Assert.Equal("code_issue", body.Draft); // DrafterAgent stubs LLM to return same fixed string in this test
    }

    [Fact]
    public async Task Query_RecordsSummedTokenUsage_AcrossAllAgentsThatRan()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.SetupSequence(l => l.LastTotalTokens)
            .Returns(10)  // Triage's call
            .Returns(25); // Drafter's call
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm), new NoOpAgent("KbResearcher"), new NoOpAgent("CodeAnalyzer"), new NoOpAgent("VisionAnalyzer"), new DrafterAgent(openAiLlm));
        var tokenUsage = new Mock<ITokenUsageRepository>();
        TokenUsageEntry? recorded = null;
        tokenUsage.Setup(t => t.AddAsync(It.IsAny<TokenUsageEntry>(), default))
            .Callback<TokenUsageEntry, CancellationToken>((entry, _) => recorded = entry)
            .Returns(Task.CompletedTask);
        var controller = MakeController(pipeline, openAiLlm, tokenUsage.Object);

        await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "Getting a 500 error" });

        Assert.NotNull(recorded);
        Assert.Equal(35, recorded!.TotalTokens);
    }

    private sealed class NoOpAgent : IAgent
    {
        public string Name { get; }
        public NoOpAgent(string name) => Name = name;
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class TestOpenAiLlmClient : OpenAiLlmClient
    {
        private readonly ILlmClient _inner;

        public TestOpenAiLlmClient(ILlmClient inner) : base(null!, null!, "unused-model")
        {
            _inner = inner;
        }

        public override int LastTotalTokens => _inner.LastTotalTokens;

        public override async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            return await _inner.CompleteAsync(systemPrompt, userPrompt, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            return await _inner.EmbedAsync(text, ct);
        }

        public override async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
        {
            return await _inner.AnalyzeImageAsync(base64Image, prompt, ct);
        }
    }
}
