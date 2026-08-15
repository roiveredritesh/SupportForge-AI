using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api;
using SupportForge.Api.Contracts;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationRepository _conversations;
    private readonly IChatMessageRepository _messages;
    private readonly IProjectMembershipRepository _memberships;
    private readonly IUserRepository _users;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        IConversationRepository conversations,
        IChatMessageRepository messages,
        IProjectMembershipRepository memberships,
        IUserRepository users,
        ILogger<ConversationsController> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _memberships = memberships;
        _users = users;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationDto>>> GetByProject(
        [FromQuery] string projectId, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), projectId, ct)) return Forbid();

        var conversations = await _conversations.GetByProjectIdAsync(projectId, ct);
        return Ok(conversations.Select(ToDto));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ConversationDetailDto>> GetById(string id, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetByIdAsync(id, ct);
        if (conversation is null) return NotFound();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), conversation.ProjectId, ct)) return Forbid();

        var messages = await _messages.GetByConversationIdAsync(id, ct);
        return Ok(new ConversationDetailDto(
            conversation.Id,
            conversation.ProjectId,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messages.Select(ToDto).ToList()));
    }

    [HttpPost]
    public async Task<ActionResult<ConversationDto>> Create(CreateConversationRequest request, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), request.ProjectId, ct)) return Forbid();

        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = request.ProjectId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New chat" : request.Title,
        };
        await _conversations.UpsertAsync(conversation, ct);
        return Ok(ToDto(conversation));
    }

    // U26: gated to L2/L3/Admin -- an L1 inviting an engineer into their own conversation is the
    // intended flow, so the gate is about who *can be invited to see code-bearing detail*, not
    // who can ask for help (Escalate above stays open to every role for that reason).
    [HttpPost("{id}/invite")]
    [Authorize(Roles = "L2,L3,Admin")]
    public async Task<ActionResult<ConversationDto>> Invite(string id, [FromBody] InviteToConversationRequest request, CancellationToken ct = default)
    {
        if (this.CurrentUserRole() is not (AppRole.L2 or AppRole.L3 or AppRole.Admin)) return Forbid();

        var conversation = await _conversations.GetByIdAsync(id, ct);
        if (conversation is null) return NotFound();

        var actorId = this.CurrentUserId();
        if (!await _memberships.IsMemberAsync(actorId, conversation.ProjectId, ct)) return Forbid();

        var invitedUser = await _users.GetByIdAsync(request.UserId, ct);
        if (invitedUser is null) return BadRequest("User not found.");

        if (!conversation.InvitedUserIds.Contains(request.UserId))
            conversation.InvitedUserIds.Add(request.UserId);
        await _conversations.UpsertAsync(conversation, ct);

        // U26/U7: same audit-logging precedent as OrgsController.RegisterEmployee -- actor, target,
        // and what changed, at the one call site that grants scoped elevated visibility.
        _logger.LogInformation(
            "ConversationInvite actor={ActorId} target={TargetId} conversation={ConversationId} at={Timestamp}",
            actorId, request.UserId, id, DateTimeOffset.UtcNow);

        return Ok(ToDto(conversation));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetByIdAsync(id, ct);
        if (conversation is null) return NotFound();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), conversation.ProjectId, ct)) return Forbid();

        await _messages.DeleteByConversationIdAsync(id, ct);
        await _conversations.DeleteAsync(id, ct);
        return NoContent();
    }

    private static ConversationDto ToDto(Conversation c) => new(c.Id, c.ProjectId, c.Title, c.CreatedAt, c.UpdatedAt, c.InvitedUserIds);

    private static ChatMessageDto ToDto(ChatMessage m) => new(
        m.Id,
        m.Role,
        m.Content,
        m.Confidence,
        m.Sources,
        m.CreatedAt,
        m.TotalTokensUsed);
}
