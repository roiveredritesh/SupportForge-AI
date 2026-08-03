using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.VectorStore;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId}/freshness")]
[Authorize]
public class FreshnessController : ControllerBase
{
    private readonly IProjectRepository _repo;
    private readonly IVectorStoreService _vectorStore;

    public FreshnessController(IProjectRepository repo, IVectorStoreService vectorStore)
    {
        _repo = repo;
        _vectorStore = vectorStore;
    }

    [HttpGet]
    public async Task<IActionResult> Get(string projectId, CancellationToken ct)
    {
        var project = await _repo.GetByIdAsync(projectId, ct);
        if (project is null) return NotFound();

        var score = FreshnessCalculator.Calculate(project);
        if (project.KbSources.Count == 0) return Ok(score);

        // The sync-state timestamps FreshnessCalculator relies on only record that a sync *ran* --
        // they don't verify the vector store still holds what it wrote (e.g. a Chroma restart/reset
        // between syncs). A project can show "Fresh" with an empty index and nothing else surfaces
        // that until a query silently comes back with 0 KB snippets. One cheap count call closes that
        // gap for the badge the admin UI actually shows.
        long kbCount;
        try
        {
            kbCount = await _vectorStore.CountAsync($"{projectId}-kb", ct);
        }
        catch
        {
            return Ok(score); // vector store unreachable -- don't fail the freshness check over it
        }

        if (kbCount > 0) return Ok(score);

        var sources = score.Sources
            .Select(s => project.KbSources.Any(k => k.Location == s.Name) ? s with { IsStale = true } : s)
            .ToList();
        var stale = sources.Where(s => s.IsStale).Select(s => s.Name).ToList();
        return Ok(new FreshnessScore(stale.Count == 0, stale, sources));
    }
}
