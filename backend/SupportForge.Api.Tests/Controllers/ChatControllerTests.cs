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
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());

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
    public async Task Query_DoesNotExposeSourcePaths_InResponseOrPersistedTurn()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("an answer");

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm),
            new SourceAddingAgent("KbResearcher", "KB: getting-started.md", "kb/getting-started.md"),
            new NoOpAgent("KbResearcherVerifier"),
            new SourceAddingAgent("CodeAnalyzer", "Code: ChatController.cs", "backend/ChatController.cs"),
            new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm));

        var vectorStore = new Mock<IVectorStoreService>();
        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var persisted = new List<ChatMessage>();
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ChatMessage, CancellationToken>((m, _) => persisted.Add(m))
            .Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());

        var controller = new ChatController(
            pipeline, new TriageAgent(openAiLlm),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object)),
            new CodeAnalyzerAgent(new CodeSearchTool(openAiLlm, vectorStore.Object)),
            new KbResearcherVerifier(openAiLlm), new CodeAnalyzerVerifier(openAiLlm),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm)), new VisionAnalyzerVerifier(openAiLlm),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var json = System.Text.Json.JsonSerializer.Serialize((ChatQueryResponse)ok.Value!);
        Assert.DoesNotContain(".md", json);
        Assert.DoesNotContain(".cs", json);

        var persistedJson = System.Text.Json.JsonSerializer.Serialize(persisted);
        Assert.DoesNotContain(".md", persistedJson);
        Assert.DoesNotContain(".cs", persistedJson);
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
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());
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

        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext { Response = { Body = responseBody } };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.QueryStream(new ChatQueryRequest { ProjectId = "proj1", Query = "how do I reset my password" }, default);

        Assert.Equal(2, callCount); // first search found nothing, retry found something

        // The retry populated context.Sources with kb/x.md — none of it may reach the wire.
        var stream = System.Text.Encoding.UTF8.GetString(responseBody.ToArray());
        Assert.DoesNotContain(".md", stream);
        Assert.DoesNotContain("sources", stream);
    }

    [Fact]
    public async Task QueryStream_LeakingDraft_PersistsFallbackAndZeroConfidence()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");
        llmMock.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });
        llmMock.Setup(l => l.StreamCompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new[] { "Here you go:\n", "```cs\nvar x = 1;\n```" }));

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(new List<VectorQueryResult>());

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var persisted = new List<ChatMessage>();
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ChatMessage, CancellationToken>((m, _) => persisted.Add(m))
            .Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());

        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm), new NoOpAgent("KbResearcher"), new NoOpAgent("KbVerifier"),
            new NoOpAgent("CodeAnalyzer"), new NoOpAgent("CodeVerifier"),
            new NoOpAgent("VisionAnalyzer"), new NoOpAgent("VisionVerifier"),
            new DrafterAgent(openAiLlm));

        var controller = new ChatController(
            pipeline, new TriageAgent(openAiLlm),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object)),
            new CodeAnalyzerAgent(new CodeSearchTool(openAiLlm, vectorStore.Object)),
            new KbResearcherVerifier(openAiLlm), new CodeAnalyzerVerifier(openAiLlm),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm)), new VisionAnalyzerVerifier(openAiLlm),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object);

        var body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = body } },
        };

        await controller.QueryStream(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" }, default);

        var assistant = Assert.Single(persisted, m => m.Role == "assistant");
        Assert.Equal(DrafterAgent.LeakFallback, assistant.Content);
        Assert.Equal(0.0, assistant.Confidence);
        Assert.Contains("\"confidence\":0", System.Text.Encoding.UTF8.GetString(body.ToArray()));
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

    private sealed class SourceAddingAgent : IAgent
    {
        private readonly (string Label, string Url) _source;
        public SourceAddingAgent(string name, string label, string url) { Name = name; _source = (label, url); }
        public string Name { get; }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            context.Sources.Add(_source);
            return Task.FromResult(context);
        }
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

        public override async IAsyncEnumerable<string> StreamCompleteAsync(string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var token in _inner.StreamCompleteAsync(systemPrompt, userPrompt, ct))
            {
                yield return token;
            }
        }
    }
}
