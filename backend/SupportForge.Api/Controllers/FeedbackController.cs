using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    private readonly IFeedbackRepository _repo;
    private readonly IProjectMembershipRepository _memberships;
    private readonly IOrgMembershipRepository _orgMemberships;
    private readonly IProjectRepository _projects;

    public FeedbackController(
        IFeedbackRepository repo, IProjectMembershipRepository memberships,
        IOrgMembershipRepository orgMemberships, IProjectRepository projects)
    {
        _repo = repo;
        _memberships = memberships;
        _orgMemberships = orgMemberships;
        _projects = projects;
    }

    // U17: Sources is the set of ChatSource.Url values shown alongside the answer this feedback is
    // about -- KbSearchTool's down-weighting counts negative votes per source. ReasonCode is
    // required when Useful is explicitly false, optional/absent otherwise.
    public sealed record SubmitRequest(
        string ProjectId, string Query, bool? Useful, bool Escalated,
        IReadOnlyList<string>? Sources = null, FeedbackReasonCode? ReasonCode = null);

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct = default)
    {
        var userId = this.CurrentUserId();
        if (!await _memberships.IsMemberAsync(userId, request.ProjectId, ct)) return Forbid();
        if (request.Useful == false && request.ReasonCode is null)
            return BadRequest("ReasonCode is required when marking feedback as not useful.");

        await _repo.AddAsync(
            new FeedbackEntry(request.ProjectId, request.Query, request.Useful, request.Escalated, DateTimeOffset.UtcNow, userId, request.Sources, request.ReasonCode),
            ct);
        return Ok();
    }

    public sealed record DashboardEntry(string ProjectId, string Query, DateTimeOffset CreatedAt, FeedbackReasonCode? ReasonCode);
    public sealed record DashboardResponse(IReadOnlyList<DashboardEntry> RecentNegative, IReadOnlyDictionary<string, int> ReasonCodeBreakdown);

    // U18: recent negative feedback + reason-code breakdown, scoped to orgs/projects the calling
    // Admin belongs to -- mirrors OrgsController.GetEmployees' org->projects scoping pattern.
    [HttpGet("dashboard")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<DashboardResponse>> Dashboard(CancellationToken ct = default)
    {
        if (this.CurrentUserRole() != AppRole.Admin) return Forbid();

        var orgIds = await _orgMemberships.GetOrgIdsForUserAsync(this.CurrentUserId(), ct);
        var allProjects = await _projects.GetAllAsync(ct);
        var scopedProjectIds = allProjects.Where(p => orgIds.Contains(p.OrgId)).Select(p => p.Id).ToHashSet();

        var allFeedback = await _repo.GetAllAsync(ct);
        var negative = allFeedback
            .Where(f => f.Useful == false && scopedProjectIds.Contains(f.ProjectId))
            .OrderByDescending(f => f.CreatedAt)
            .ToList();

        var breakdown = negative
            .Where(f => f.ReasonCode is not null)
            .GroupBy(f => f.ReasonCode!.Value)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return Ok(new DashboardResponse(
            negative.Select(f => new DashboardEntry(f.ProjectId, f.Query, f.CreatedAt, f.ReasonCode)).ToList(),
            breakdown));
    }
}
