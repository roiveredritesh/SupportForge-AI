using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/ingestion")]
[Authorize]
public class IngestionController : ControllerBase
{
    private readonly IngestionQueue _queue;
    private readonly IProjectRepository _projects;
    private readonly IProjectMembershipRepository _memberships;
    private readonly IDeadLetterRepository _deadLetters;
    private readonly IServiceProvider _services;

    public IngestionController(
        IngestionQueue queue, IProjectRepository projects, IProjectMembershipRepository memberships,
        IDeadLetterRepository deadLetters, IServiceProvider services)
    {
        _queue = queue;
        _projects = projects;
        _memberships = memberships;
        _deadLetters = deadLetters;
        _services = services;
    }

    public sealed record TriggerRequest(string ProjectId);

    [HttpPost("trigger")]
    public async Task<IActionResult> Trigger([FromBody] TriggerRequest request, CancellationToken ct)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), request.ProjectId, ct)) return Forbid();

        var project = await _projects.GetByIdAsync(request.ProjectId, ct);
        if (project is null) return NotFound();

        // Without this, a double-click (or an impatient re-trigger) queues a fully redundant
        // clone+extract+graph-import per repo on top of the one already running -- no error, just
        // wasted work racing itself.
        if (_queue.IsBusy(request.ProjectId))
            return Conflict("Ingestion is already running for this project.");

        // Task 7 / Task 8 register the concrete job factories that read `project.KbSources` / `project.Repos`.
        foreach (var factory in _services.GetServices<IIngestionJobFactory>())
            foreach (var job in factory.CreateJobs(project))
                _queue.Enqueue(job);

        return Accepted();
    }

    // C7 (gap-closing-solutions.md Phase C, item 7): visibility into permanently-failed ingestion
    // jobs. No "requeue this specific job" action -- the original job object isn't retained after
    // failure, and the existing Trigger/"Re-index" endpoint already re-runs the whole project, which
    // is the practical remedy.
    [HttpGet("dead-letters")]
    public async Task<ActionResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLetters([FromQuery] string projectId, CancellationToken ct)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), projectId, ct)) return Forbid();

        return Ok(await _deadLetters.GetByProjectIdAsync(projectId, ct));
    }

    [HttpDelete("dead-letters/{id}")]
    public async Task<IActionResult> DismissDeadLetter(string id, CancellationToken ct)
    {
        var entry = await _deadLetters.GetByIdAsync(id, ct);
        if (entry is null) return NotFound();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), entry.ProjectId, ct)) return Forbid();

        await _deadLetters.DeleteAsync(id, ct);
        return NoContent();
    }
}
