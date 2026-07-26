using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _repo;
    private readonly IVectorStoreService _vectorStore;
    private readonly IFeedbackRepository _feedback;
    private readonly ITokenUsageRepository _tokenUsage;
    private readonly IConversationRepository _conversations;
    private readonly IWebHostEnvironment _env;

    public ProjectsController(
        IProjectRepository repo,
        IVectorStoreService vectorStore,
        IFeedbackRepository feedback,
        ITokenUsageRepository tokenUsage,
        IConversationRepository conversations,
        IWebHostEnvironment env)
    {
        _repo = repo;
        _vectorStore = vectorStore;
        _feedback = feedback;
        _tokenUsage = tokenUsage;
        _conversations = conversations;
        _env = env;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Project>>> GetAll(CancellationToken ct = default)
        => Ok(await _repo.GetAllAsync(ct));

    [HttpPost]
    public async Task<ActionResult<Project>> CreateOrUpdate(Project project, CancellationToken ct = default)
    {
        await _repo.UpsertAsync(project, ct);
        return Ok(project);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var project = await _repo.GetByIdAsync(id, ct);
        if (project is null) return NotFound();

        // Cleanup runs before the project row is removed so a failure here leaves the project
        // (and this endpoint) retryable instead of orphaning its data with no way to find it again.
        await _vectorStore.DeleteCollectionAsync($"{id}-kb", ct);
        await _vectorStore.DeleteCollectionAsync($"{id}-code", ct);

        var repoDir = Path.Combine(_env.ContentRootPath, "App_Data", "repos", id);
        if (Directory.Exists(repoDir)) Directory.Delete(repoDir, recursive: true);

        await _feedback.DeleteByProjectIdAsync(id, ct);
        await _tokenUsage.DeleteByProjectIdAsync(id, ct);
        await _conversations.DeleteByProjectIdAsync(id, ct);

        await _repo.DeleteAsync(id, ct);
        return NoContent();
    }
}
