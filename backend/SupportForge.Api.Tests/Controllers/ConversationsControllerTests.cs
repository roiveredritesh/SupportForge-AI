using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Api.Contracts;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// U26: POST /api/conversations/{id}/invite -- adds an invited user to Conversation.InvitedUserIds,
// gated to L2/L3/Admin (an L1 inviting an engineer into their own conversation is the intended
// flow; the gate is about who can be invited to see code-bearing detail, not who can ask for help).
public class ConversationsControllerTests
{
    private static ClaimsPrincipal TestUser(string userId, AppRole role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role.ToString()) }, "TestAuth"));

    private static ConversationsController MakeController(
        IConversationRepository conversations, IProjectMembershipRepository? memberships, IUserRepository? users, string userId, AppRole role)
    {
        var controller = new ConversationsController(
            conversations,
            new Mock<IChatMessageRepository>().Object,
            memberships ?? MakePermissiveMemberships(),
            users ?? MakeExistingUsers(),
            NullLogger<ConversationsController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser(userId, role) } };
        return controller;
    }

    private static IProjectMembershipRepository MakePermissiveMemberships()
    {
        var mock = new Mock<IProjectMembershipRepository>();
        mock.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock.Object;
    }

    private static IUserRepository MakeExistingUsers()
    {
        var mock = new Mock<IUserRepository>();
        mock.Setup(u => u.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new AppUser { Id = id, UserName = id });
        return mock.Object;
    }

    private static Mock<IConversationRepository> MakeConversationsWithSeed(Conversation seeded)
    {
        var mock = new Mock<IConversationRepository>();
        mock.Setup(c => c.GetByIdAsync(seeded.Id, It.IsAny<CancellationToken>())).ReturnsAsync(seeded);
        mock.Setup(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    [Theory]
    [InlineData(AppRole.L2)]
    [InlineData(AppRole.L3)]
    [InlineData(AppRole.Admin)]
    public async Task Invite_L2L3AdminCaller_AddsUserToInvitedList(AppRole role)
    {
        var conversation = new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" };
        var conversations = MakeConversationsWithSeed(conversation);
        var controller = MakeController(conversations.Object, null, null, "caller", role);

        var response = await controller.Invite("conv1", new InviteToConversationRequest("engineer-1"));

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<ConversationDto>(ok.Value);
        Assert.Contains("engineer-1", dto.InvitedUserIds);
        conversations.Verify(c => c.UpsertAsync(It.Is<Conversation>(x => x.InvitedUserIds.Contains("engineer-1")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Invite_L1Caller_ReturnsForbid()
    {
        var conversation = new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" };
        var conversations = MakeConversationsWithSeed(conversation);
        var controller = MakeController(conversations.Object, null, null, "caller", AppRole.L1);

        var response = await controller.Invite("conv1", new InviteToConversationRequest("engineer-1"));

        Assert.IsType<ForbidResult>(response.Result);
        conversations.Verify(c => c.UpsertAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Invite_CallerNotProjectMember_ReturnsForbid()
    {
        var conversation = new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" };
        var conversations = MakeConversationsWithSeed(conversation);
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = MakeController(conversations.Object, memberships.Object, null, "caller", AppRole.L3);

        var response = await controller.Invite("conv1", new InviteToConversationRequest("engineer-1"));

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task Invite_ConversationNotFound_ReturnsNotFound()
    {
        var conversations = new Mock<IConversationRepository>();
        conversations.Setup(c => c.GetByIdAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((Conversation?)null);
        var controller = MakeController(conversations.Object, null, null, "caller", AppRole.L3);

        var response = await controller.Invite("missing", new InviteToConversationRequest("engineer-1"));

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task Invite_InvitedUserDoesNotExist_ReturnsBadRequest()
    {
        var conversation = new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" };
        var conversations = MakeConversationsWithSeed(conversation);
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AppUser?)null);
        var controller = MakeController(conversations.Object, null, users.Object, "caller", AppRole.L3);

        var response = await controller.Invite("conv1", new InviteToConversationRequest("ghost"));

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task Invite_SameUserTwice_DoesNotDuplicate()
    {
        var conversation = new Conversation { Id = "conv1", ProjectId = "proj1", Title = "t" };
        var conversations = MakeConversationsWithSeed(conversation);
        var controller = MakeController(conversations.Object, null, null, "caller", AppRole.L3);

        await controller.Invite("conv1", new InviteToConversationRequest("engineer-1"));
        var response = await controller.Invite("conv1", new InviteToConversationRequest("engineer-1"));

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<ConversationDto>(ok.Value);
        Assert.Single(dto.InvitedUserIds, id => id == "engineer-1");
    }
}
