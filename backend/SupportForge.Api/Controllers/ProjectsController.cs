using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
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
    private readonly IngestionQueue _ingestionQueue;

    public ProjectsController(
        IProjectRepository repo,
        IVectorStoreService vectorStore,
        IFeedbackRepository feedback,
        ITokenUsageRepository tokenUsage,
        IConversationRepository conversations,
        IWebHostEnvironment env,
        IngestionQueue ingestionQueue)
    {
        _repo = repo;
        _vectorStore = vectorStore;
        _feedback = feedback;
        _tokenUsage = tokenUsage;
        _conversations = conversations;
        _env = env;
        _ingestionQueue = ingestionQueue;
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

        // A code-sync (POST /api/ingestion/trigger, or an automatic re-sync) may still be
        // cloning/reading this project's repo on the background ingestion worker; deleting
        // out from under it is a real, long-lived lock that no amount of retrying will out-wait.
        await _ingestionQueue.WaitUntilIdleAsync(id, TimeSpan.FromSeconds(30), ct);

        var repoDir = Path.Combine(_env.ContentRootPath, "App_Data", "repos", id);
        await DeleteRepoDirWithRetryAsync(repoDir);

        await _feedback.DeleteByProjectIdAsync(id, ct);
        await _tokenUsage.DeleteByProjectIdAsync(id, ct);
        await _conversations.DeleteByProjectIdAsync(id, ct);

        await _repo.DeleteAsync(id, ct);
        return NoContent();
    }

    // ponytail: libgit2 (via LibGit2Sharp) memory-maps pack/idx files on Windows and doesn't
    // always release the mapping the instant a Repository is disposed, so a recursive delete
    // run right after a clone/pull can hit a still-locked pack file. Retry with a GC nudge
    // (forces native finalizers to run) instead of a fixed sleep; give up after a few tries so
    // a genuinely stuck lock still surfaces as a real error.
    private static async Task DeleteRepoDirWithRetryAsync(string repoDir, int maxAttempts = 3)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!Directory.Exists(repoDir)) return;

            try
            {
                Directory.Delete(repoDir, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < maxAttempts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200 * attempt);
            }
        }
    }
}
