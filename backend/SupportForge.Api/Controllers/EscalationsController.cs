using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

// U19: escalate/claim/queue/my-issues. The critical design point is POST .../escalate: it builds
// the handoff Markdown from state ChatController already cached on the conversation's last
// assistant ChatMessage (KbSnippets/CodeSnippets/VisionFindings/ProductVersion/Config -- see
// ChatMessage.cs and ChatController.RecordTurnAsync) instead of re-running the agent pipeline. This
// controller has no dependency on CoordinatorPipeline, any agent, or an LLM client at all -- there
// is nothing here that could spend a second round of tokens even by accident.
[ApiController]
[Authorize]
public class EscalationsController : ControllerBase
{
    private readonly IEscalationRepository _escalations;
    private readonly IConversationRepository _conversations;
    private readonly IChatMessageRepository _messages;
    private readonly IProjectMembershipRepository _memberships;

    public EscalationsController(
        IEscalationRepository escalations, IConversationRepository conversations,
        IChatMessageRepository messages, IProjectMembershipRepository memberships)
    {
        _escalations = escalations;
        _conversations = conversations;
        _messages = messages;
        _memberships = memberships;
    }

    public sealed record EscalateResponse(string EscalationId, EscalationStatus Status);

    // Every authenticated role including L1 -- L1 is the persona that most needs "easy escalation"
    // (PRD-2.0). No role restriction here, only project membership.
    [HttpPost("api/conversations/{id}/escalate")]
    public async Task<ActionResult<EscalateResponse>> Escalate(string id, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetByIdAsync(id, ct);
        if (conversation is null) return NotFound();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), conversation.ProjectId, ct)) return Forbid();

        var history = await _messages.GetByConversationIdAsync(id, ct);
        var assistant = history.LastOrDefault(m => m.Role == "assistant");
        if (assistant is null) return BadRequest("Conversation has no answered turn to escalate yet.");
        var query = history.LastOrDefault(m => m.Role == "user" && m.CreatedAt <= assistant.CreatedAt)?.Content
                    ?? history.LastOrDefault(m => m.Role == "user")?.Content
                    ?? "(no question text found)";

        var escalation = new Escalation(
            Id: Guid.NewGuid().ToString("n"),
            ConversationId: id,
            ProjectId: conversation.ProjectId,
            EscalatedByUserId: this.CurrentUserId(),
            EscalatedAt: DateTimeOffset.UtcNow,
            Markdown: BuildMarkdown(query, assistant),
            Status: EscalationStatus.Open);
        await _escalations.UpsertAsync(escalation, ct);

        return Ok(new EscalateResponse(escalation.Id, escalation.Status));
    }

    // Assembled once, from already-computed state -- see the class-level comment above.
    private static string BuildMarkdown(string query, ChatMessage assistant)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Escalated Conversation");
        sb.AppendLine();
        sb.AppendLine($"**Question:** {query}");
        sb.AppendLine();
        sb.AppendLine("**Draft Answer:**");
        sb.AppendLine(assistant.Content);

        if (assistant.ProductVersion is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"**Product Version:** {assistant.ProductVersion}");
        }
        if (assistant.Config is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("**Config:** " + string.Join(", ", assistant.Config.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        if (assistant.KbSnippets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### KB Findings");
            foreach (var s in assistant.KbSnippets) sb.AppendLine($"- {s}");
        }
        if (assistant.CodeSnippets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Code Findings");
            foreach (var s in assistant.CodeSnippets) sb.AppendLine($"- {s}");
        }
        if (assistant.VisionFindings is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("### Vision Findings");
            sb.AppendLine(assistant.VisionFindings);
        }
        return sb.ToString();
    }

    // Lists open escalations across projects the caller belongs to.
    [HttpGet("api/escalations")]
    [Authorize(Roles = "L2,L3,Admin")]
    public async Task<ActionResult<IReadOnlyList<Escalation>>> Queue(CancellationToken ct = default)
    {
        if (this.CurrentUserRole() is not (AppRole.L2 or AppRole.L3 or AppRole.Admin)) return Forbid();

        var projectIds = await _memberships.GetProjectIdsForUserAsync(this.CurrentUserId(), ct);
        var all = await _escalations.GetAllAsync(ct);
        return Ok(all.Where(e => e.Status == EscalationStatus.Open && projectIds.Contains(e.ProjectId))
            .OrderByDescending(e => e.EscalatedAt).ToList());
    }

    [HttpPost("api/escalations/{id}/claim")]
    [Authorize(Roles = "L2,L3,Admin")]
    public async Task<ActionResult<Escalation>> Claim(string id, CancellationToken ct = default)
    {
        if (this.CurrentUserRole() is not (AppRole.L2 or AppRole.L3 or AppRole.Admin)) return Forbid();

        var escalation = await _escalations.GetByIdAsync(id, ct);
        if (escalation is null) return NotFound();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), escalation.ProjectId, ct)) return Forbid();

        var claimed = escalation with
        {
            Status = EscalationStatus.Claimed,
            ClaimedByUserId = this.CurrentUserId(),
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        await _escalations.UpsertAsync(claimed, ct);
        return Ok(claimed);
    }

    // Currently-claimed (open) + recently-resolved escalations for the calling engineer.
    [HttpGet("api/engineers/me/issues")]
    public async Task<ActionResult<IReadOnlyList<Escalation>>> MyIssues(CancellationToken ct = default)
    {
        var userId = this.CurrentUserId();
        var all = await _escalations.GetAllAsync(ct);
        return Ok(all
            .Where(e => e.ClaimedByUserId == userId && e.Status is EscalationStatus.Claimed or EscalationStatus.Resolved)
            .OrderByDescending(e => e.ClaimedAt)
            .ToList());
    }
}
