using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Ingestion;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/ingestion")]
[Authorize]
public class IngestionController : ControllerBase
{
    private readonly IngestionQueue _queue;
    private readonly IProjectRepository _projects;
    private readonly IServiceProvider _services;

    public IngestionController(IngestionQueue queue, IProjectRepository projects, IServiceProvider services)
    {
        _queue = queue;
        _projects = projects;
        _services = services;
    }

    public sealed record TriggerRequest(string ProjectId);

    [HttpPost("trigger")]
    public async Task<IActionResult> Trigger([FromBody] TriggerRequest request, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(request.ProjectId, ct);
        if (project is null) return NotFound();

        // Task 7 / Task 8 register the concrete job factories that read `project.KbSources` / `project.Repos`.
        foreach (var factory in _services.GetServices<IIngestionJobFactory>())
            foreach (var job in factory.CreateJobs(project))
                _queue.Enqueue(job);

        return Accepted();
    }
}
