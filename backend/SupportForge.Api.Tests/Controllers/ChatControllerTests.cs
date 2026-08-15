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

public class ChatControllerTests
{
    // B1: these tests exercise agent/pipeline behavior, not project-access control (that's covered by
    // ProjectAccessTests) -- a fixed test user plus a membership repo that always says "yes" keeps every
    // existing test's intent unchanged now that Query/QueryStream check membership before doing any work.
    private const string TestUserId = "test-user";

    private static ClaimsPrincipal TestUser() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, TestUserId) }, "TestAuth"));

    // U6: same identity, but carrying a Role claim -- CurrentUserRole() (ControllerBaseExtensions)
    // is what ChatController.CodeDetailsForRole gates on.
    private static ClaimsPrincipal TestUser(SupportForge.Core.Entities.AppRole role) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, TestUserId), new Claim(ClaimTypes.Role, role.ToString()) }, "TestAuth"));

    private static IProjectMembershipRepository MakePermissiveMemberships()
    {
        var mock = new Mock<IProjectMembershipRepository>();
        mock.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock.Object;
    }

    // U11: "no project found" is a safe default everywhere these tests don't specifically exercise
    // CommitLookupTool -- CommitHistoryForRoleAsync short-circuits to null on a null project.
    private static IProjectRepository MakeNoProjectRepository()
    {
        var mock = new Mock<IProjectRepository>();
        mock.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);
        return mock.Object;
    }

    private static CommitLookupTool MakeCommitLookup() => new(new HttpClient(), null, Path.GetTempPath());

    // U24: an empty blast radius is a safe default everywhere these tests don't specifically
    // exercise BlastRadiusForRoleAsync -- same "no-op stand-in" shape as MakeCommitLookup above.
    private static IBlastRadiusQueryTool MakeEmptyBlastRadius()
    {
        var mock = new Mock<IBlastRadiusQueryTool>();
        mock.Setup(b => b.QueryAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BlastRadiusEntry>());
        return mock.Object;
    }

    // U17: no negative feedback recorded -- the default stand-in for every test here, none of which
    // exercise KbSearchTool's down-weighting (that's covered by KbSearchToolTests).
    private static IFeedbackRepository MakeEmptyFeedbackRepository()
    {
        var mock = new Mock<IFeedbackRepository>();
        mock.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<FeedbackEntry>());
        return mock.Object;
    }

    // WS3 (retrieval-pipeline remediation plan): a project repo returning null (no project found) and an
    // ingestion-activity check that's never busy -- FreshnessGateAgent handles both gracefully, so this is
    // a safe stand-in everywhere these tests don't care about freshness behavior specifically.
    private static FreshnessGateAgent MakeFreshnessGateAgent()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);
        var ingestionActivity = new Mock<IIngestionActivity>();
        ingestionActivity.Setup(i => i.IsBusy(It.IsAny<string>())).Returns(false);
        return new FreshnessGateAgent(projects.Object, ingestionActivity.Object, NullLogger<FreshnessGateAgent>.Instance);
    }

    // WS4 (retrieval-pipeline remediation plan): a real CrossReferenceAgent instance around a stubbed
    // LLM client -- ChatController's constructor requires the concrete type, same as every other agent.
    private static CrossReferenceAgent MakeCrossReferenceAgent(ILlmClient llm) =>
        new(llm, NullLogger<CrossReferenceAgent>.Instance);

    private static ChatController MakeController(
        CoordinatorPipeline pipeline, ILlmClient llm, ITokenUsageRepository tokenUsage, IProjectMembershipRepository? memberships = null)
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

        // U11: a project repo returning null (no project found) is a safe default everywhere these
        // tests don't specifically exercise CommitLookupTool -- CommitHistoryForRoleAsync treats
        // "no project" the same as "no commit history to attach", same short-circuit shape as
        // MakeFreshnessGateAgent's stand-in above.
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
            memberships ?? MakePermissiveMemberships(),
            projects.Object,
            commitLookup,
            MakeEmptyBlastRadius(),
            NullLogger<ChatController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser() } };
        return controller;
    }

    [Fact]
    public async Task Query_ReturnsDraftAndConfidence_FromPipeline()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");

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
        var tokenUsage = new Mock<ITokenUsageRepository>();
        var controller = MakeController(pipeline, openAiLlm, tokenUsage.Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "Getting a 500 error" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        Assert.Equal("code_issue", body.Draft); // DrafterAgent stubs LLM to return same fixed string in this test
    }

    [Fact]
    public async Task Query_CallerNotProjectMember_ReturnsForbid()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(TestUserId, "someone-elses-project", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object, memberships.Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "someone-elses-project", Query = "hello" });

        Assert.IsType<ForbidResult>(response.Result);
    }

    private static (CoordinatorPipeline pipeline, TestOpenAiLlmClient llm) MakeNoOpPipeline()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("ok");
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
        return (pipeline, openAiLlm);
    }

    [Fact]
    public async Task Query_WithQueryWithinLimit_Proceeds()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = new string('a', 4000) });

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task Query_WithQueryOverLimit_ReturnsBadRequest()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = new string('a', 4001) });

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("4000", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Query_WithScreenshotWithinSizeLimit_Proceeds()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object);
        var screenshot = Convert.ToBase64String(new byte[5 * 1024 * 1024]);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "q", ScreenshotBase64 = screenshot });

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task Query_WithScreenshotOverSizeLimit_ReturnsBadRequest()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object);
        var screenshot = Convert.ToBase64String(new byte[5 * 1024 * 1024 + 1]);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "q", ScreenshotBase64 = screenshot });

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task Query_WithMalformedBase64Screenshot_ReturnsBadRequest_NotUnhandledException()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "q", ScreenshotBase64 = "not-valid-base64!!" });

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("base64", badRequest.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryStream_WithQueryOverLimit_Writes400_NotUnhandledException()
    {
        var (pipeline, llm) = MakeNoOpPipeline();
        var controller = MakeController(pipeline, llm, new Mock<ITokenUsageRepository>().Object);
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() }, User = TestUser() };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.QueryStream(new ChatQueryRequest { ProjectId = "proj1", Query = new string('a', 4001) }, default);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Query_DoesNotExposeSourcePaths_InResponseOrPersistedTurn()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("an answer");

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance),
            new NoOpAgent("FreshnessGate"),
            new SourceAddingAgent("KbResearcher", "KB: getting-started.md", "kb/getting-started.md"),
            new NoOpAgent("KbResearcherVerifier"),
            new NoOpAgent("CrossReference"),
            new SourceAddingAgent("CodeAnalyzer", "Code: ChatController.cs", "backend/ChatController.cs"),
            new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));

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
            pipeline, new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), MakeFreshnessGateAgent(),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance),
            MakeCrossReferenceAgent(openAiLlm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance),
            new KbResearcherVerifier(openAiLlm, NullLogger<KbResearcherVerifier>.Instance), new CodeAnalyzerVerifier(openAiLlm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm), NullLogger<VisionAnalyzerAgent>.Instance), new VisionAnalyzerVerifier(openAiLlm, NullLogger<VisionAnalyzerVerifier>.Instance),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object, MakePermissiveMemberships(), MakeNoProjectRepository(), MakeCommitLookup(), MakeEmptyBlastRadius(), NullLogger<ChatController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser() } };

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var json = System.Text.Json.JsonSerializer.Serialize((ChatQueryResponse)ok.Value!);
        Assert.DoesNotContain(".md", json);
        Assert.DoesNotContain(".cs", json);

        var persistedJson = System.Text.Json.JsonSerializer.Serialize(persisted);
        Assert.DoesNotContain(".md", persistedJson);
        Assert.DoesNotContain(".cs", persistedJson);
    }

    // E1 (gap-closing-solutions.md Phase E): the positive case for BuildSources -- a branch whose
    // verifier actually passes DOES surface its sources in the response and persisted turn. Paired
    // with the test above (rejected/no-op branches correctly show none), this proves the filter is
    // driven by verification status, not a blanket allow/deny.
    [Fact]
    public async Task Query_PassedVerification_IncludesSourcesInResponseAndPersistedTurn()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("an answer");

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance),
            new NoOpAgent("FreshnessGate"),
            new SourceAddingAgent("KbResearcher", "KB: getting-started.md", "kb/getting-started.md"),
            new PassingVerifierAgent("KbResearcherVerifier", c => c.KbVerification.Status = VerificationStatus.Passed),
            new NoOpAgent("CrossReference"),
            new SourceAddingAgent("CodeAnalyzer", "Code: project graph", "proj1"),
            new NoOpAgent("CodeAnalyzerVerifier"), // stays NotRun -- its source must NOT appear
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));

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
            pipeline, new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), MakeFreshnessGateAgent(),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance),
            MakeCrossReferenceAgent(openAiLlm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance),
            new KbResearcherVerifier(openAiLlm, NullLogger<KbResearcherVerifier>.Instance), new CodeAnalyzerVerifier(openAiLlm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm), NullLogger<VisionAnalyzerAgent>.Instance), new VisionAnalyzerVerifier(openAiLlm, NullLogger<VisionAnalyzerVerifier>.Instance),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object, MakePermissiveMemberships(), MakeNoProjectRepository(), MakeCommitLookup(), MakeEmptyBlastRadius(), NullLogger<ChatController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser() } };

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        var source = Assert.Single(body.Sources);
        Assert.Equal("KB: getting-started.md", source.Label);

        var assistantMessage = Assert.Single(persisted, m => m.Role == "assistant");
        var persistedSource = Assert.Single(assistantMessage.Sources);
        Assert.Equal("KB: getting-started.md", persistedSource.Label);
    }

    // U6: the sprint's core trust guarantee -- L1 never receives CodeAnalyzerAgent's raw findings
    // in the chat response, even when the agent actually found matches.
    [Fact]
    public async Task Query_L1Caller_NeverReceivesCodeDetails_EvenWhenCodeAnalyzerFoundMatches()
    {
        var (controller, _) = MakeControllerWithCodeFindings(SupportForge.Core.Entities.AppRole.L1);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        Assert.Null(body.CodeDetails);

        // Not just null in the C# object -- entirely absent from the wire (JsonIgnore WhenWritingNull).
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.DoesNotContain("codeDetails", json, StringComparison.OrdinalIgnoreCase);

        // U6 explicitly out of scope: DrafterAgent's leak guard is untouched, so the drafted answer
        // text itself stays code-blind for every role regardless of what CodeAnalyzer found.
        Assert.DoesNotContain(".cs", body.Draft);
    }

    [Theory]
    [InlineData(SupportForge.Core.Entities.AppRole.L2)]
    [InlineData(SupportForge.Core.Entities.AppRole.L3)]
    [InlineData(SupportForge.Core.Entities.AppRole.Admin)]
    public async Task Query_L2L3AdminCaller_ReceivesCodeDetails_WhenCodeAnalyzerFoundMatches(SupportForge.Core.Entities.AppRole role)
    {
        var (controller, _) = MakeControllerWithCodeFindings(role);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        var detail = Assert.Single(body.CodeDetails!);
        Assert.Contains("ChatController.cs", detail);
    }

    // U24: same trust guarantee as U6's CodeDetails test -- L1 never receives BlastRadiusQueryTool's
    // findings in the chat response, even when the tool actually found a real cross-repo match.
    [Fact]
    public async Task Query_L1Caller_NeverReceivesBlastRadius_EvenWhenToolFoundAMatch()
    {
        var blastRadiusMock = new Mock<IBlastRadiusQueryTool>();
        blastRadiusMock.Setup(b => b.QueryAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BlastRadiusEntry> { new("repo-a", new List<string> { "repo-b" }) });
        var (controller, _) = MakeControllerWithCodeFindings(SupportForge.Core.Entities.AppRole.L1, blastRadiusMock.Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        Assert.Null(body.BlastRadius);

        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.DoesNotContain("blastRadius", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SupportForge.Core.Entities.AppRole.L2)]
    [InlineData(SupportForge.Core.Entities.AppRole.L3)]
    [InlineData(SupportForge.Core.Entities.AppRole.Admin)]
    public async Task Query_L2L3AdminCaller_ReceivesBlastRadius_WhenToolFoundAMatch(SupportForge.Core.Entities.AppRole role)
    {
        var blastRadiusMock = new Mock<IBlastRadiusQueryTool>();
        blastRadiusMock.Setup(b => b.QueryAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BlastRadiusEntry> { new("repo-a", new List<string> { "repo-b", "repo-c" }) });
        var (controller, _) = MakeControllerWithCodeFindings(role, blastRadiusMock.Object);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "why does this fail" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        var entry = Assert.Single(body.BlastRadius!);
        Assert.Equal("repo-a", entry.Repo);
        Assert.Equal(new[] { "repo-b", "repo-c" }, entry.UsedBy);
    }

    private static (ChatController Controller, CoordinatorPipeline Pipeline) MakeControllerWithCodeFindings(
        SupportForge.Core.Entities.AppRole role, IBlastRadiusQueryTool? blastRadius = null)
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("an answer");
        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);

        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance),
            new NoOpAgent("FreshnessGate"),
            new NoOpAgent("KbResearcher"),
            new NoOpAgent("KbResearcherVerifier"),
            new NoOpAgent("CrossReference"),
            new CodeSnippetAddingAgent(),
            new NoOpAgent("CodeAnalyzerVerifier"),
            new NoOpAgent("VisionAnalyzer"),
            new NoOpAgent("VisionAnalyzerVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));

        var vectorStore = new Mock<IVectorStoreService>();
        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ChatMessage>());

        var controller = new ChatController(
            pipeline, new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), MakeFreshnessGateAgent(),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance),
            MakeCrossReferenceAgent(openAiLlm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance),
            new KbResearcherVerifier(openAiLlm, NullLogger<KbResearcherVerifier>.Instance), new CodeAnalyzerVerifier(openAiLlm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm), NullLogger<VisionAnalyzerAgent>.Instance), new VisionAnalyzerVerifier(openAiLlm, NullLogger<VisionAnalyzerVerifier>.Instance),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object, MakePermissiveMemberships(), MakeNoProjectRepository(), MakeCommitLookup(), blastRadius ?? MakeEmptyBlastRadius(), NullLogger<ChatController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser(role) } };

        return (controller, pipeline);
    }

    [Fact]
    public async Task Query_RecordsSummedTokenUsage_AcrossAllAgentsThatRan()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.As<ILlmChatClient>().SetupSequence(l => l.LastTotalTokens)
            .Returns(10)  // Triage's call
            .Returns(25); // Drafter's call
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");

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
        llmMock.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
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

        var kbResearcher = new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance);
        var kbVerifier = new KbResearcherVerifier(openAiLlm, NullLogger<KbResearcherVerifier>.Instance);

        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.AddAsync(It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        messages.Setup(m => m.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessage>());
        var tokenUsage = new Mock<ITokenUsageRepository>();

        var pipeline = new CoordinatorPipeline(
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), new NoOpAgent("FreshnessGate"), new NoOpAgent("KbResearcher"), new NoOpAgent("KbVerifier"), new NoOpAgent("CrossReference"),
            new NoOpAgent("CodeAnalyzer"), new NoOpAgent("CodeVerifier"),
            new NoOpAgent("VisionAnalyzer"), new NoOpAgent("VisionVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));

        var controller = new ChatController(
            pipeline, new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), MakeFreshnessGateAgent(), kbResearcher,
            MakeCrossReferenceAgent(openAiLlm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance), kbVerifier,
            new CodeAnalyzerVerifier(openAiLlm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm), NullLogger<VisionAnalyzerAgent>.Instance), new VisionAnalyzerVerifier(openAiLlm, NullLogger<VisionAnalyzerVerifier>.Instance),
            openAiLlm, tokenUsage.Object, conversations.Object, messages.Object, MakePermissiveMemberships(), MakeNoProjectRepository(), MakeCommitLookup(), MakeEmptyBlastRadius(), NullLogger<ChatController>.Instance);

        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext { Response = { Body = responseBody }, User = TestUser() };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.QueryStream(new ChatQueryRequest { ProjectId = "proj1", Query = "how do I reset my password" }, default);

        Assert.Equal(2, callCount); // first search found nothing, retry found something

        // The retry populated context.Sources with kb/x.md, but the LLM judge's response never
        // parses as a valid snippet index in this test (llmMock unconditionally returns
        // "kb_question" from every CompleteAsync call), so KbVerification never reaches Passed --
        // BuildSources (E1: gap-closing-solutions.md Phase E) correctly excludes it. ".md" must not
        // reach the wire either way; sources being present as an empty array (not the leaked value)
        // is the precise proof, not "the literal word doesn't appear anywhere".
        var stream = System.Text.Encoding.UTF8.GetString(responseBody.ToArray());
        Assert.DoesNotContain(".md", stream);
        Assert.Contains("\"sources\":[]", stream);
    }

    [Fact]
    public async Task QueryStream_LeakingDraft_PersistsFallbackAndZeroConfidence()
    {
        var llmMock = new Mock<ILlmClient>();
        llmMock.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("code_issue");
        // The judge calls (KbResearcherVerifier/CodeAnalyzerVerifier) share CompleteAsync with Triage's
        // intent classification above -- this more specific setup (matched by the judge's distinguishing
        // "You judge" system prompt text) makes KB verification pass so DrafterAgent.HasNoUsableContext
        // doesn't short-circuit before the leak check this test exists to exercise.
        llmMock.Setup(l => l.CompleteAsync(It.Is<string>(s => s.Contains("You judge")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1");
        llmMock.Setup(l => l.EmbedAsync(It.IsAny<string>(), default, It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        llmMock.Setup(l => l.StreamCompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new[] { "Here you go:\n", "```cs\nvar x = 1;\n```" }));

        var openAiLlm = new TestOpenAiLlmClient(llmMock.Object);
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "some KB text", 0.1f, new Dictionary<string, string> { ["source"] = "faq.md" }) });

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
            new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), new NoOpAgent("FreshnessGate"), new NoOpAgent("KbResearcher"), new NoOpAgent("KbVerifier"), new NoOpAgent("CrossReference"),
            new NoOpAgent("CodeAnalyzer"), new NoOpAgent("CodeVerifier"),
            new NoOpAgent("VisionAnalyzer"), new NoOpAgent("VisionVerifier"),
            new DrafterAgent(openAiLlm, NullLogger<DrafterAgent>.Instance));

        var controller = new ChatController(
            pipeline, new TriageAgent(openAiLlm, NullLogger<TriageAgent>.Instance), MakeFreshnessGateAgent(),
            new KbResearcherAgent(new KbSearchTool(openAiLlm, vectorStore.Object, MakeEmptyFeedbackRepository()), NullLogger<KbResearcherAgent>.Instance),
            MakeCrossReferenceAgent(openAiLlm),
            new CodeAnalyzerAgent(new Mock<ICodeGraphQueryTool>().Object, NullLogger<CodeAnalyzerAgent>.Instance),
            new KbResearcherVerifier(openAiLlm, NullLogger<KbResearcherVerifier>.Instance), new CodeAnalyzerVerifier(openAiLlm, NullLogger<CodeAnalyzerVerifier>.Instance),
            new VisionAnalyzerAgent(new VisionAnalysisTool(openAiLlm), NullLogger<VisionAnalyzerAgent>.Instance), new VisionAnalyzerVerifier(openAiLlm, NullLogger<VisionAnalyzerVerifier>.Instance),
            openAiLlm, new Mock<ITokenUsageRepository>().Object, conversations.Object, messages.Object, MakePermissiveMemberships(), MakeNoProjectRepository(), MakeCommitLookup(), MakeEmptyBlastRadius(), NullLogger<ChatController>.Instance);

        var body = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = body }, User = TestUser() },
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

    // U6: stands in for a real CodeAnalyzerAgent run that found matches -- populates
    // context.CodeSnippets the same way CodeAnalyzerAgent does, so these tests exercise
    // ChatController's role gate on real (if fake) findings, not an empty list.
    private sealed class CodeSnippetAddingAgent : IAgent
    {
        public string Name => "CodeAnalyzer";
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            // U11/U24: matches ChatController's CodeLocationRef regex (src=<file> loc=L<line>) so
            // ExtractFilePaths finds a file both CommitHistoryForRoleAsync and BlastRadiusForRoleAsync
            // key off of, not just the "contains ChatController.cs" substring these tests assert on.
            context.CodeSnippets.Add("NODE Query [src=ChatController.cs loc=L219]: Query action");
            return Task.FromResult(context);
        }
    }

    private sealed class PassingVerifierAgent : IAgent
    {
        private readonly Action<AgentContext> _markPassed;
        public PassingVerifierAgent(string name, Action<AgentContext> markPassed) { Name = name; _markPassed = markPassed; }
        public string Name { get; }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            _markPassed(context);
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
