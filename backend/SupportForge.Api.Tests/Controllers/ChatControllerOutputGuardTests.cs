using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Guard;
using SupportForge.Agents.Tools;
using SupportForge.Api.Controllers;
using SupportForge.Api.Contracts;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// U19 (R27), streaming path: the buffered draft in QueryStream gets the same output guard as DrafterAgent.
// The helpers are copies of the ones in ChatControllerTests and ChatControllerIntentPersistenceTests, so those files stay untouched.
public class ChatControllerOutputGuardTests
{
    private const string TestUserId = "test-user";

    // Two chunks that pass LooksLikeLeak, so the draft reaches the output guard.
    private static readonly string[] CleanChunks = { "This is expected: ", "empty carts are rejected before payment." };
    private const string CleanDraft = "This is expected: empty carts are rejected before payment.";

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

    // Mocks the output guard's Jev layer. Scores disclosure at the given value and follows-injected at zero.
    private static Mock<IOutputGuard> OutputGuardReturning(double disclosure)
    {
        var guard = new Mock<IOutputGuard>();
        guard.Setup(g => g.AssessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Dictionary<string, double>
             {
                 [OutputGuardSignals.Disclosure] = disclosure,
                 [OutputGuardSignals.FollowsInjected] = 0.0,
             });
        return guard;
    }

    private static Mock<ILlmClient> StreamingLlm()
    {
        var llmMock = new Mock<ILlmClient>();
        // Triage answers "code_issue". The judge calls share CompleteAsync, and the more specific "You judge" setup
        // below makes KB verification pass, so the streaming Drafter is reached rather than the no-context fallback.
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");
        llmMock.Setup(l => l.CompleteAsync(It.Is<string>(s => s.Contains("You judge")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1");
        llmMock.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        llmMock.Setup(l => l.StreamCompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(CleanChunks));
        return llmMock;
    }

    private static ChatController MakeController(Mock<ILlmClient> llmMock, OutputGuardService? outputGuard, List<ChatMessage> persisted)
    {
        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "some KB text", 0.1f, new Dictionary<string, string> { ["source"] = "faq.md" }) });

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ChatMessage, CancellationToken>((m, _) => persisted.Add(m))
            .Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());

        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);

        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance),
            new NoOpAgent("FreshnessGate"), new NoOpAgent("KbResearcher"), new NoOpAgent("KbResearcherVerifier"), new NoOpAgent("CrossReference"),
            new NoOpAgent("CodeAnalyzer"), new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"), new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));

        return new ChatController(
            pipeline, new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), MakeFreshnessGateAgent(),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance),
            MakeCrossReferenceAgent(openAiLlm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance),
            new KbResearcherVerifier(openAiLlm, NullLogger<KbResearcherVerifier>.Instance),
            new CodeAnalyzerVerifier(openAiLlm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm), NullLogger<VisionAnalyzerAgent>.Instance),
            new VisionAnalyzerVerifier(openAiLlm, NullLogger<VisionAnalyzerVerifier>.Instance),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object,
            MakePermissiveMemberships(), projects.Object, new CommitLookupTool(new HttpClient(), null, Path.GetTempPath()),
            MakeEmptyBlastRadius(), NullLogger<ChatController>.Instance,
            guard: null, outputGuard: outputGuard);
    }

    // Runs QueryStream and returns the body, so each test can check what the client was sent.
    private static async Task<MemoryStream> RunStreamAsync(ChatController controller)
    {
        var body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = body }, User = TestUser() },
        };

        await controller.QueryStream(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" }, default);
        return body;
    }

    // Each word arrives as its own "data: \"...\"" event, so the text is reassembled from them.
    private static string Streamed(MemoryStream body)
    {
        var stream = System.Text.Encoding.UTF8.GetString(body.ToArray());
        return string.Concat(stream.Split('\n')
            .Where(line => line.StartsWith("data: \"", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<string>(line["data: ".Length..])!));
    }

    [Fact]
    public async Task QueryStream_OutputGuardEnforceFired_StreamsAndPersistsLeakFallback_WithZeroConfidence()
    {
        var llmMock = StreamingLlm();
        var guard = OutputGuardReturning(disclosure: 0.95);
        var persisted = new List<ChatMessage>();
        var outputGuard = new OutputGuardService(new GuardOptions { Mode = GuardMode.Enforce }, guard.Object, NullLogger<OutputGuardService>.Instance);

        var body = await RunStreamAsync(MakeController(llmMock, outputGuard, persisted));

        guard.Verify(g => g.AssessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(DrafterAgent.LeakFallback, Streamed(body));
        var assistant = Assert.Single(persisted, m => m.Role == "assistant");
        Assert.Equal(DrafterAgent.LeakFallback, assistant.Content);
        Assert.Equal(0.0, assistant.Confidence);
        Assert.Contains("\"confidence\":0", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task QueryStream_OutputGuardShadowFired_StreamsAndPersistsTheDraft()
    {
        var llmMock = StreamingLlm();
        var guard = OutputGuardReturning(disclosure: 0.95);
        var persisted = new List<ChatMessage>();
        var outputGuard = new OutputGuardService(new GuardOptions { Mode = GuardMode.Shadow }, guard.Object, NullLogger<OutputGuardService>.Instance);

        var body = await RunStreamAsync(MakeController(llmMock, outputGuard, persisted));

        guard.Verify(g => g.AssessAsync(It.IsAny<AgentContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(CleanDraft, Streamed(body));
        var assistant = Assert.Single(persisted, m => m.Role == "assistant");
        Assert.Equal(CleanDraft, assistant.Content);
        Assert.True(assistant.Confidence > 0.0);
    }

    [Fact]
    public async Task QueryStream_NoOutputGuard_StreamsTheDraftUnchanged()
    {
        var llmMock = StreamingLlm();
        var persisted = new List<ChatMessage>();

        var body = await RunStreamAsync(MakeController(llmMock, outputGuard: null, persisted: persisted));

        Assert.Equal(CleanDraft, Streamed(body));
        var assistant = Assert.Single(persisted, m => m.Role == "assistant");
        Assert.Equal(CleanDraft, assistant.Content);
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
