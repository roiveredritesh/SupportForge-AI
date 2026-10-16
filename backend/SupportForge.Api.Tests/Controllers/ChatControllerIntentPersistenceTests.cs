using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
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

// R10 (JEV intent classification plan): the triage intent, confidence and source are stored on the assistant message.
// The helpers below are copies of the ones in ChatControllerTests, so that file stays untouched.
public class ChatControllerIntentPersistenceTests
{
    private const string TestUserId = "test-user";

    private static ClaimsPrincipal TestUser() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, TestUserId) }, "TestAuth"));

    private static IProjectMembershipRepository MakePermissiveMemberships()
    {
        var mock = new Mock<IProjectMembershipRepository>();
        mock.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock.Object;
    }

    private static IFeedbackRepository MakeEmptyFeedbackRepository()
    {
        var mock = new Mock<IFeedbackRepository>();
        mock.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<FeedbackEntry>());
        return mock.Object;
    }

    private static IBlastRadiusQueryTool MakeEmptyBlastRadius()
    {
        var mock = new Mock<IBlastRadiusQueryTool>();
        mock.Setup(b => b.QueryAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BlastRadiusEntry>());
        return mock.Object;
    }

    private static FreshnessGateAgent MakeFreshnessGateAgent()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);
        var ingestionActivity = new Mock<IIngestionActivity>();
        ingestionActivity.Setup(i => i.IsBusy(It.IsAny<string>())).Returns(false);
        return new FreshnessGateAgent(projects.Object, ingestionActivity.Object, NullLogger<FreshnessGateAgent>.Instance);
    }

    private static CrossReferenceAgent MakeCrossReferenceAgent(ILlmClient llm) =>
        new(llm, NullLogger<CrossReferenceAgent>.Instance);

    // Same wiring as ChatControllerTests.MakeController with no guard and no history. Every persisted message is
    // captured in `persisted` so the test can inspect the stored turn.
    private static ChatController MakeController(
        CoordinatorPipeline pipeline, ILlmClient llm, ITokenUsageRepository tokenUsage, List<ChatMessage> persisted)
    {
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(new List<VectorQueryResult>());

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ChatMessage, CancellationToken>((m, _) => persisted.Add(m))
            .Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());

        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);
        var commitLookup = new CommitLookupTool(new HttpClient(), null, Path.GetTempPath());

        var controller = new ChatController(
            pipeline,
            new TriageAgent(llm, NullLogger<TriageAgent>.Instance),
            MakeFreshnessGateAgent(),
            new KbResearcherAgent(new KbSearchTool(llm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance),
            MakeCrossReferenceAgent(llm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance),
            new KbResearcherVerifier(llm, NullLogger<KbResearcherVerifier>.Instance),
            new CodeAnalyzerVerifier(llm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(llm), NullLogger<VisionAnalyzerAgent>.Instance),
            new VisionAnalyzerVerifier(llm, NullLogger<VisionAnalyzerVerifier>.Instance),
            llm,
            tokenUsage,
            conversations.Object,
            messages.Object,
            MakePermissiveMemberships(),
            projects.Object,
            commitLookup,
            MakeEmptyBlastRadius(),
            NullLogger<ChatController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser() } };
        return controller;
    }

    [Fact]
    public async Task Query_PersistsTriageIntentConfidenceAndSourceOnAssistantMessage()
    {
        // The LLM answers "kb_question" for every call, so triage's classifier produces that intent.
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("kb_question");
        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance),
            new NoOpAgent("FreshnessGate"),
            new NoOpAgent("KbResearcher"),
            new NoOpAgent("KbResearcherVerifier"),
            new NoOpAgent("CrossReference"),
            new NoOpAgent("CodeAnalyzer"),
            new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));
        var persisted = new List<ChatMessage>();
        var controller = MakeController(pipeline, openAiLlm, new Mock<ITokenUsageRepository>().Object, persisted);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "How do I export a report?" });

        Assert.IsType<OkObjectResult>(response.Result);
        var assistant = Assert.Single(persisted, m => m.Role == "assistant");
        Assert.Equal("kb_question", assistant.Intent);
        Assert.Equal<double?>(1.0, assistant.IntentConfidence);
        Assert.Equal("llm", assistant.IntentSource);
        // The customer's own message is not an assistant turn, so it carries no triage fields.
        var user = Assert.Single(persisted, m => m.Role == "user");
        Assert.Null(user.Intent);
        Assert.Null(user.IntentConfidence);
        Assert.Null(user.IntentSource);
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

        public override int LastTotalTokens => ((ILlmChatClient)_inner).LastTotalTokens;

        public override async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            return await _inner.CompleteAsync(systemPrompt, userPrompt, ct);
        }

        public override async Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query)
        {
            return await _inner.EmbedAsync(text, ct, purpose);
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
