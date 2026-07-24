using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId}/freshness")]
public class FreshnessController : ControllerBase
{
    private readonly IProjectRepository _repo;

    public FreshnessController(IProjectRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> Get(string projectId, CancellationToken ct)
    {
        var project = await _repo.GetByIdAsync(projectId, ct);
        if (project is null) return NotFound();

        return Ok(FreshnessCalculator.Calculate(project));
    }
}
