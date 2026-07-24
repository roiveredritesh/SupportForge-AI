using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _repo;

    public ProjectsController(IProjectRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Project>>> GetAll(CancellationToken ct = default)
        => Ok(await _repo.GetAllAsync(ct));

    [HttpPost]
    public async Task<ActionResult<Project>> CreateOrUpdate(Project project, CancellationToken ct = default)
    {
        await _repo.UpsertAsync(project, ct);
        return Ok(project);
    }
}
