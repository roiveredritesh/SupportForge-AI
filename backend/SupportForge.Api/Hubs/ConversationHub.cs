using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SupportForge.Core;

namespace SupportForge.Api.Hubs;

// U26: one SignalR group per Conversation.Id. Join/leave is gated by the same effective-access
// rule ChatController's IsElevated uses (project member OR invited into this one conversation) --
// so a caller who can't pass that check can never receive this conversation's broadcasts either.
// ChatController pushes the "new message" event into a conversation's group after each turn
// (IHubContext<ConversationHub>); this class itself only owns join/leave + presence.
[Authorize]
public class ConversationHub : Hub
{
    private readonly IConversationRepository _conversations;
    private readonly IProjectMembershipRepository _memberships;
    private readonly IConversationPresenceTracker _presence;

    public ConversationHub(IConversationRepository conversations, IProjectMembershipRepository memberships, IConversationPresenceTracker presence)
    {
        _conversations = conversations;
        _memberships = memberships;
        _presence = presence;
    }

    public static string GroupName(string conversationId) => $"conversation:{conversationId}";

    private string CurrentUserId() =>
        Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("Unauthenticated connection.");

    private async Task<bool> CanAccessAsync(string conversationId)
    {
        var conversation = await _conversations.GetByIdAsync(conversationId);
        if (conversation is null) return false;

        var userId = CurrentUserId();
        return await _memberships.IsMemberAsync(userId, conversation.ProjectId) || conversation.InvitedUserIds.Contains(userId);
    }

    public async Task JoinConversation(string conversationId)
    {
        if (!await CanAccessAsync(conversationId)) throw new HubException("Not authorized for this conversation.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversationId));
        var userIds = _presence.Join(conversationId, Context.ConnectionId, CurrentUserId());
        await Clients.Group(GroupName(conversationId)).SendAsync("presence", new { conversationId, userIds });
    }

    public async Task LeaveConversation(string conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(conversationId));
        await BroadcastLeaveAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await BroadcastLeaveAsync();
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastLeaveAsync()
    {
        if (_presence.Leave(Context.ConnectionId) is not { } result) return;
        await Clients.Group(GroupName(result.ConversationId))
            .SendAsync("presence", new { conversationId = result.ConversationId, userIds = result.UserIds });
    }
}
