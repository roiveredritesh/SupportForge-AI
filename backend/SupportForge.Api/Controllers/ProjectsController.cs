using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api;
using SupportForge.Api.Contracts;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using SupportForge.VectorStore;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _repo;
    private readonly IProjectMembershipRepository _memberships;
    private readonly IOrgMembershipRepository _orgMemberships;
    private readonly IVectorStoreService _vectorStore;
    private readonly IFeedbackRepository _feedback;
    private readonly ITokenUsageRepository _tokenUsage;
    private readonly IConversationRepository _conversations;
    private readonly IDeadLetterRepository _deadLetters;
    private readonly IContentHashRepository _contentHashes;
    private readonly IEscalationRepository _escalations;
    private readonly IWebHostEnvironment _env;
    private readonly IngestionQueue _ingestionQueue;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(
        IProjectRepository repo,
        IProjectMembershipRepository memberships,
        IOrgMembershipRepository orgMemberships,
        IVectorStoreService vectorStore,
        IFeedbackRepository feedback,
        ITokenUsageRepository tokenUsage,
        IConversationRepository conversations,
        IDeadLetterRepository deadLetters,
        IContentHashRepository contentHashes,
        IEscalationRepository escalations,
        IWebHostEnvironment env,
        IngestionQueue ingestionQueue,
        ILogger<ProjectsController> logger)
    {
        _repo = repo;
        _memberships = memberships;
        _orgMemberships = orgMemberships;
        _vectorStore = vectorStore;
        _feedback = feedback;
        _tokenUsage = tokenUsage;
        _conversations = conversations;
        _deadLetters = deadLetters;
        _contentHashes = contentHashes;
        _escalations = escalations;
        _env = env;
        _ingestionQueue = ingestionQueue;
        _logger = logger;
    }

    // B1: lists only the projects the caller is a member of, not every project system-wide.
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Project>>> GetAll(CancellationToken ct = default)
    {
        var memberProjectIds = await _memberships.GetProjectIdsForUserAsync(this.CurrentUserId(), ct);
        var all = await _repo.GetAllAsync(ct);
        return Ok(all.Where(p => memberProjectIds.Contains(p.Id)).ToList());
    }

    // Lets the frontend show token spend per project, broken down by where it went -- "chat"
    // (agent pipeline answering queries) vs "ingestion" (embedding calls during KB register/reindex).
    [HttpGet("{id}/token-usage")]
    public async Task<ActionResult<TokenUsageSummary>> GetTokenUsage(string id, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), id, ct)) return Forbid();

        var bySource = await _tokenUsage.GetTotalsBySourceForProjectAsync(id, ct);
        return Ok(new TokenUsageSummary(bySource.Values.Sum(), bySource));
    }

    // U4: chat query volume by day, last 30 days -- feeds the Dashboard's line chart.
    [HttpGet("{id}/query-volume")]
    public async Task<ActionResult<IReadOnlyList<QueryVolumePoint>>> GetQueryVolume(string id, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), id, ct)) return Forbid();

        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var entries = await _tokenUsage.GetEntriesForProjectAsync(id, ct);
        var byDay = entries
            .Where(e => e.Source == "chat" && e.CreatedAt >= since)
            .GroupBy(e => e.CreatedAt.UtcDateTime.Date)
            .OrderBy(g => g.Key)
            .Select(g => new QueryVolumePoint(g.Key.ToString("yyyy-MM-dd"), g.Count()));
        return Ok(byDay.ToList());
    }

    // U4: useful vs. not-useful feedback counts for the project -- role-open (any member), distinct
    // from FeedbackController.Dashboard which is Admin-only and org-scoped.
    [HttpGet("{id}/feedback-summary")]
    public async Task<ActionResult<FeedbackSummary>> GetFeedbackSummary(string id, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), id, ct)) return Forbid();

        var all = await _feedback.GetAllAsync(ct);
        var forProject = all.Where(f => f.ProjectId == id).ToList();
        return Ok(new FeedbackSummary(
            forProject.Count(f => f.Useful == true),
            forProject.Count(f => f.Useful == false)));
    }

    // U4: open/claimed/resolved escalation counts for the project -- mirrors
    // EscalationsController.Queue's existing in-memory-filter-by-project pattern.
    [HttpGet("{id}/escalation-stats")]
    public async Task<ActionResult<EscalationStats>> GetEscalationStats(string id, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), id, ct)) return Forbid();

        var all = await _escalations.GetAllAsync(ct);
        var forProject = all.Where(e => e.ProjectId == id).ToList();
        return Ok(new EscalationStats(
            forProject.Count(e => e.Status == EscalationStatus.Open),
            forProject.Count(e => e.Status == EscalationStatus.Claimed),
            forProject.Count(e => e.Status == EscalationStatus.Resolved)));
    }

    // B1: project creation stays self-service (agreed design) -- any authenticated user can create a
    // project and is auto-granted membership. Updating an *existing* project id requires the caller
    // already be a member, so a non-member can't silently take over another org's project by reusing
    // its id in a POST body.
    [HttpPost]
    public async Task<ActionResult<Project>> CreateOrUpdate(Project project, CancellationToken ct = default)
    {
        var userId = this.CurrentUserId();
        var existing = await _repo.GetByIdAsync(project.Id, ct);
        if (existing is not null && !await _memberships.IsMemberAsync(userId, project.Id, ct))
            return Forbid();

        // OrgId is stamped server-side, never trusted from the client -- a new project belongs to
        // the caller's own org (first membership; project creation itself doesn't support choosing
        // among multiple), and an update keeps whatever OrgId the project already had regardless of
        // what the client posted, so a member can't reassign a project to a different org by editing
        // the body. Without this, ProjectOrgMigration only ever backfills projects that existed
        // before it ran -- every project created afterward would silently keep OrgId null forever.
        string? orgId;
        if (existing is null)
        {
            var callerOrgIds = await _orgMemberships.GetOrgIdsForUserAsync(userId, ct);
            orgId = callerOrgIds.FirstOrDefault();
        }
        else
        {
            orgId = existing.OrgId;
        }

        var toSave = new Project
        {
            Id = project.Id,
            Name = project.Name,
            OrgId = orgId,
            Repos = project.Repos,
            KbSources = project.KbSources,
            CreatedAt = project.CreatedAt,
            ScheduledSyncIntervalHours = project.ScheduledSyncIntervalHours,
            CodeClassificationEnabled = project.CodeClassificationEnabled,
        };

        await _repo.UpsertAsync(toSave, ct);
        project = toSave;
        if (existing is null) await _memberships.AddAsync(userId, project.Id, ct);
        return Ok(project);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), id, ct)) return Forbid();

        var project = await _repo.GetByIdAsync(id, ct);
        if (project is null) return NotFound();

        // Cleanup runs before the project row is removed so a failure here leaves the project
        // (and this endpoint) retryable instead of orphaning its data with no way to find it again.
        await _vectorStore.DeleteCollectionAsync($"{id}-kb", ct);

        // A code-sync (POST /api/ingestion/trigger, or an automatic re-sync) may still be
        // cloning/reading this project's repo on the background ingestion worker; deleting
        // out from under it is a real, long-lived lock that no amount of retrying will out-wait.
        await _ingestionQueue.WaitUntilIdleAsync(id, TimeSpan.FromSeconds(30), ct);

        var repoDir = Path.Combine(_env.ContentRootPath, "App_Data", "repos", id);
        try
        {
            await DeleteRepoDirWithRetryAsync(repoDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // ponytail: on Windows this lock isn't always the transient LibGit2Sharp pack-file mmap
            // the retry above targets -- a search indexer or AV scan can hold a directory handle open
            // far longer than any bounded retry should wait. That must not make project deletion
            // itself unrecoverable: the KB/feedback/token/conversation data below is still fully
            // cleaned up and the project record is still removed. The directory is orphaned on disk
            // (not in app state) and can be cleared out manually later.
            _logger.LogWarning(ex,
                "Could not delete repo directory {RepoDir} for project {ProjectId}; leaving it on disk and continuing with project deletion",
                repoDir, id);
        }

        await _feedback.DeleteByProjectIdAsync(id, ct);
        await _tokenUsage.DeleteByProjectIdAsync(id, ct);
        await _conversations.DeleteByProjectIdAsync(id, ct);
        await _memberships.DeleteByProjectIdAsync(id, ct);
        await _deadLetters.DeleteByProjectIdAsync(id, ct);
        await _contentHashes.DeleteByProjectIdAsync(id, ct);
        await _escalations.DeleteByProjectIdAsync(id, ct);

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
