using Microsoft.AspNetCore.Http;
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

        return new ChatController(
            pipeline,
            new TriageAgent(llm),
            new KbResearcherAgent(new KbSearchTool(llm, vectorStore.Object)),
            new CodeAnalyzerAgent(new CodeSearchTool(llm, vectorStore.Object)),
            new KbResearcherVerifier(llm),
            new CodeAnalyzerVerifier(llm),
            new VisionAnalyzerAgent(new VisionAnalysisTool(llm)),
            new VisionAnalyzerVerifier(llm),
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
            new TriageAgent(openAiLlm),
            new NoOpAgent("KbResearcher"),
            new NoOpAgent("KbResearcherVerifier"),
            new NoOpAgent("CodeAnalyzer"),
            new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm));
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
            new TriageAgent(openAiLlm),
            new NoOpAgent("KbResearcher"),
            new NoOpAgent("KbResearcherVerifier"),
            new NoOpAgent("CodeAnalyzer"),
            new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm));
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

    [Fact]
    public async Task QueryStream_KbVerificationFails_RetriesOnceBeforeDrafting()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("kb_question");
        llmMock.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });
        llmMock.SetupSequence(l => l.StreamCompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new[] { "answer" }));

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var vectorStore = new Mock<IVectorStoreService>();
        var callCount = 0;
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<VectorQueryResult>() // first attempt: nothing found -> verifier fails
                    : new List<VectorQueryResult> { new("doc-1", "found on retry", 0.2f, new Dictionary<string, string> { ["source"] = "kb/x.md" }) };
            });

        var kbResearcher = new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object));
        var kbVerifier = new KbResearcherVerifier(openAiLlm);

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var tokenUsage = new Mock<ITokenUsageRepository>();

        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm), new NoOpAgent("KbResearcher"), new NoOpAgent("KbVerifier"),
            new NoOpAgent("CodeAnalyzer"), new NoOpAgent("CodeVerifier"),
            new NoOpAgent("VisionAnalyzer"), new NoOpAgent("VisionVerifier"),
            new DrafterAgent(openAiLlm));

        var controller = new ChatController(
            pipeline, new TriageAgent(openAiLlm), kbResearcher,
            new CodeAnalyzerAgent(new CodeSearchTool(openAiLlm, vectorStore.Object)), kbVerifier,
            new CodeAnalyzerVerifier(openAiLlm),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm)), new VisionAnalyzerVerifier(openAiLlm),
            openAiLlm, tokenUsage.Object, conversations.Object, messages.Object);

        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.QueryStream(new ChatQueryRequest { ProjectId = "proj1", Query = "how do I reset my password" }, default);

        Assert.Equal(2, callCount); // first search found nothing, retry found something
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(IEnumerable<string> items)
    {
        foreach (var item in items) { yield return item; await Task.Yield(); }
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

        public override async IAsyncEnumerable<string> StreamCompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            await foreach (var token in _inner.StreamCompleteAsync(systemPrompt, userPrompt, ct))
            {
                yield return token;
            }
        }
    }
}
