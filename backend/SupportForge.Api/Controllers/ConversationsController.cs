using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public ConversationsController(IConversationRepository conversations, IChatMessageRepository messages)
    {
        _conversations = conversations;
        _messages = messages;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationDto>>> GetByProject(
        [FromQuery] string projectId, CancellationToken ct = default)
    {
        var conversations = await _conversations.GetByProjectIdAsync(projectId, ct);
        return Ok(conversations.Select(ToDto));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ConversationDetailDto>> GetById(string id, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetByIdAsync(id, ct);
        if (conversation is null) return NotFound();

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
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("n"),
            ProjectId = request.ProjectId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New chat" : request.Title,
        };
        await _conversations.UpsertAsync(conversation, ct);
        return Ok(ToDto(conversation));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        await _messages.DeleteByConversationIdAsync(id, ct);
        await _conversations.DeleteAsync(id, ct);
        return NoContent();
    }

    private static ConversationDto ToDto(Conversation c) => new(c.Id, c.ProjectId, c.Title, c.CreatedAt, c.UpdatedAt);

    private static ChatMessageDto ToDto(ChatMessage m) => new(
        m.Id,
        m.Role,
        m.Content,
        m.Confidence,
        m.CreatedAt);
}
