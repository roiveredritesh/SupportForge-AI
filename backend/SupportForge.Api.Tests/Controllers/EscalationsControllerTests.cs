using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SupportForge.Agents;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class EscalationsControllerTests
{
    private static ClaimsPrincipal TestUser(string userId, AppRole role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role.ToString()) }, "TestAuth"));

    private static EscalationsController MakeController(
        IEscalationRepository escalations, IConversationRepository conversations, IChatMessageRepository messages,
        IProjectMembershipRepository? memberships, string userId, AppRole role)
    {
        var controller = new EscalationsController(escalations, conversations, messages, memberships ?? MakePermissiveMemberships());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser(userId, role) } };
        return controller;
    }

    private static IProjectMembershipRepository MakePermissiveMemberships()
    {
        var mock = new Mock<IProjectMembershipRepository>();
        mock.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mock.Setup(m => m.GetProjectIdsForUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "proj1" });
        return mock.Object;
    }

    // U19: the class-level design guarantee -- EscalationsController has no way to invoke the LLM
    // or re-run the agent pipeline because it depends on neither. If a future change wires one in
    // (defeating the "assemble once, cache, never re-run" contract this sprint exists to protect),
    // this constructor-signature check catches it even before a behavioral test would.
    [Fact]
    public void Constructor_HasNoLlmOrPipelineDependency()
    {
        var ctor = typeof(EscalationsController).GetConstructors().Single();
        var forbidden = new[] { typeof(ILlmClient), typeof(ILlmChatClient), typeof(CoordinatorPipeline) };

        foreach (var param in ctor.GetParameters())
        {
            Assert.DoesNotContain(forbidden, f => f.IsAssignableFrom(param.ParameterType));
            Assert.False(typeof(IAgent).IsAssignableFrom(param.ParameterType), $"{param.ParameterType} looks like an agent -- escalation must not re-run the pipeline.");
        }
    }

    private static ChatMessage MakeAssistantMessageWithFullDetail() => new()
    {
        Id = "msg-assistant",
        ConversationId = "conv1",
        Role = "assistant",
        Content = "Here's a partial answer.",
        CreatedAt = DateTimeOffset.UtcNow,
        KbSnippets = new List<string> { "KB snippet: reset your password via Settings" },
        CodeSnippets = new List<string> { "backend/SupportForge.Api/Controllers/ChatController.cs:219 -- BuildSources" },
        VisionFindings = "Screenshot shows a 500 error banner.",
        ProductVersion = "3.0",
    };

    // U19: the sprint's core trust guarantee -- an L1-triggered escalation's cached Markdown
    // contains the full code findings/blast-radius detail even though L1's own chat response
    // (ChatQueryResponse.CodeDetails) never showed it -- same precedent as U6.
    [Fact]
    public async Task Escalate_L1Caller_CachedMarkdownContainsFullCodeAndKbDetail()
    {
        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.GetByIdAsync("conv1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" });

        var userMsg = new ChatMessage { Id = "msg-user", ConversationId = "conv1", Role = "user", Content = "why does this fail", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var assistantMsg = MakeAssistantMessageWithFullDetail();
        var messages = new Mock<IChatMessageRepository>();
        messages.Setup(m => m.GetByConversationIdAsync("conv1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<ChatMessage> { userMsg, assistantMsg });

        Escalation? saved = null;
        var escalations = new Mock<IEscalationRepository>();
        escalations.Setup(e => e.UpsertAsync(It.IsAny<Escalation>(), It.IsAny<CancellationToken>()))
            .Callback<Escalation, CancellationToken>((e, _) => saved = e)
            .Returns(Task.CompletedTask);

        var controller = MakeController(escalations.Object, conversations.Object, messages.Object, null, "l1-user", AppRole.L1);

        var response = await controller.Escalate("conv1");

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.NotNull(saved);
        Assert.Contains("ChatController.cs", saved!.Markdown);
        Assert.Contains("reset your password via Settings", saved.Markdown);
        Assert.Contains("500 error banner", saved.Markdown);
        Assert.Contains("why does this fail", saved.Markdown);
        Assert.Equal(EscalationStatus.Open, saved.Status);
        Assert.Equal("l1-user", saved.EscalatedByUserId);
        Assert.Equal("proj1", saved.ProjectId);
    }

    [Fact]
    public async Task Escalate_CallerNotProjectMember_ReturnsForbid()
    {
        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.GetByIdAsync("conv1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" });
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var controller = MakeController(new Mock<IEscalationRepository>().Object, conversations.Object, new Mock<IChatMessageRepository>().Object, memberships.Object, "l1-user", AppRole.L1);

        var response = await controller.Escalate("conv1");

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task Queue_L3Caller_ListsOpenEscalations_ScopedToOwnProjects()
    {
        var open1 = new Escalation("e1", "conv1", "proj1", "l1-user", DateTimeOffset.UtcNow, "md1", EscalationStatus.Open);
        var openOtherProject = new Escalation("e2", "conv2", "proj-not-mine", "l1-user", DateTimeOffset.UtcNow, "md2", EscalationStatus.Open);
        var claimed = new Escalation("e3", "conv3", "proj1", "l1-user", DateTimeOffset.UtcNow, "md3", EscalationStatus.Claimed, "l3-user");

        var escalations = new Mock<IEscalationRepository>();
        escalations.Setup(e => e.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Escalation> { open1, openOtherProject, claimed });

        var controller = MakeController(escalations.Object, new Mock<IConversationRepository>().Object, new Mock<IChatMessageRepository>().Object, null, "l3-user", AppRole.L3);

        var response = await controller.Queue();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<Escalation>>(ok.Value);
        var only = Assert.Single(list);
        Assert.Equal("e1", only.Id);
    }

    [Fact]
    public async Task Queue_L1Caller_ReturnsForbid()
    {
        var controller = MakeController(new Mock<IEscalationRepository>().Object, new Mock<IConversationRepository>().Object, new Mock<IChatMessageRepository>().Object, null, "l1-user", AppRole.L1);

        var response = await controller.Queue();

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task Claim_L3Caller_FlipsStatusAndSetsClaimant_AndAppearsInMyIssues()
    {
        var open = new Escalation("e1", "conv1", "proj1", "l1-user", DateTimeOffset.UtcNow, "md", EscalationStatus.Open);
        Escalation? saved = null;
        var escalations = new Mock<IEscalationRepository>();
        escalations.Setup(e => e.GetByIdAsync("e1", It.IsAny<CancellationToken>())).ReturnsAsync(() => saved ?? open);
        escalations.Setup(e => e.UpsertAsync(It.IsAny<Escalation>(), It.IsAny<CancellationToken>()))
            .Callback<Escalation, CancellationToken>((e, _) => saved = e)
            .Returns(Task.CompletedTask);

        var controller = MakeController(escalations.Object, new Mock<IConversationRepository>().Object, new Mock<IChatMessageRepository>().Object, null, "l3-user", AppRole.L3);

        var claimResponse = await controller.Claim("e1");

        var ok = Assert.IsType<OkObjectResult>(claimResponse.Result);
        var claimedEscalation = Assert.IsType<Escalation>(ok.Value);
        Assert.Equal(EscalationStatus.Claimed, claimedEscalation.Status);
        Assert.Equal("l3-user", claimedEscalation.ClaimedByUserId);

        escalations.Setup(e => e.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Escalation> { saved! });
        var myIssuesResponse = await controller.MyIssues();
        var myIssuesOk = Assert.IsType<OkObjectResult>(myIssuesResponse.Result);
        var myIssues = Assert.IsAssignableFrom<IReadOnlyList<Escalation>>(myIssuesOk.Value);
        Assert.Single(myIssues, e => e.Id == "e1");
    }
}
