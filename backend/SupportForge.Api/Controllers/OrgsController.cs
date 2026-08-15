using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

// Sprint 0 (U3): mirrors ProjectsController's list/create-or-update shape and self-service
// authorization pattern. Sprint 1 (U4/U7) adds the Admin-gated employee-registration endpoint --
// creating an *org* stays self-service (unchanged from Sprint 0); registering an *employee* into
// an existing org is the new Admin-only action.
[ApiController]
[Route("api/orgs")]
[Authorize]
public class OrgsController : ControllerBase
{
    private readonly IOrgRepository _repo;
    private readonly IOrgMembershipRepository _memberships;
    private readonly IProjectRepository _projects;
    private readonly IProjectMembershipRepository _projectMemberships;
    private readonly IUserRepository _users;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<OrgsController> _logger;

    public OrgsController(
        IOrgRepository repo,
        IOrgMembershipRepository memberships,
        IProjectRepository projects,
        IProjectMembershipRepository projectMemberships,
        IUserRepository users,
        UserManager<AppUser> userManager,
        ILogger<OrgsController> logger)
    {
        _repo = repo;
        _memberships = memberships;
        _projects = projects;
        _projectMemberships = projectMemberships;
        _users = users;
        _userManager = userManager;
        _logger = logger;
    }

    // Lists only the orgs the caller is a member of, not every org system-wide.
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Org>>> GetAll(CancellationToken ct = default)
    {
        var memberOrgIds = await _memberships.GetOrgIdsForUserAsync(this.CurrentUserId(), ct);
        var all = await _repo.GetAllAsync(ct);
        return Ok(all.Where(o => memberOrgIds.Contains(o.Id)).ToList());
    }

    // Org creation stays self-service, same as projects: any authenticated user can create an org
    // and is auto-granted membership. Updating an *existing* org id requires the caller already be
    // a member, so a non-member can't silently take over another org by reusing its id in a POST body.
    [HttpPost]
    public async Task<ActionResult<Org>> CreateOrUpdate(Org org, CancellationToken ct = default)
    {
        var userId = this.CurrentUserId();
        var existing = await _repo.GetByIdAsync(org.Id, ct);
        if (existing is not null && !await _memberships.IsMemberAsync(userId, org.Id, ct))
            return Forbid();

        await _repo.UpsertAsync(org, ct);
        if (existing is null) await _memberships.AddAsync(userId, org.Id, ct);
        return Ok(org);
    }

    public sealed record RegisterEmployeeRequest(string UserName, string Password, AppRole Role, List<string> ProjectIds);
    public sealed record EmployeeSummary(string Id, string UserName, AppRole Role, IReadOnlyList<string> ProjectIds);

    // U8: lists the org's employees (name, role, project scope) for AdminPage's Employees section.
    // "Employee" here means every user with a ProjectMembership row on one of this org's projects --
    // there's no separate employee-roster entity, membership rows are the source of truth.
    // Also the data source for U27's "Invite Engineer" picker (ChatPage.tsx's useEmployees call) --
    // L2/L3 need this to invite a colleague into a conversation, so the gate is any org member with
    // elevated access, not Admin-only. Registering a *new* employee (below) stays Admin-only; this
    // is read-only org-roster visibility, which L2/L3 already implicitly have via the escalation
    // queue and code-detail gating.
    [HttpGet("{orgId}/employees")]
    [Authorize(Roles = "L2,L3,Admin")]
    public async Task<ActionResult<IReadOnlyList<EmployeeSummary>>> GetEmployees(string orgId, CancellationToken ct = default)
    {
        // Belt-and-suspenders with [Authorize(Roles="L2,L3,Admin")]: that attribute is enforced by
        // the MVC pipeline (a no-op when the action is invoked directly, e.g. from a unit test), so
        // the explicit check here is what actually makes "L1 -> 403" true at the action level too.
        var role = this.CurrentUserRole();
        if (role != AppRole.L2 && role != AppRole.L3 && role != AppRole.Admin) return Forbid();

        var allProjects = await _projects.GetAllAsync(ct);
        var orgProjectIds = allProjects.Where(p => p.OrgId == orgId).Select(p => p.Id).ToHashSet();

        // Admins get an OrgMembership row (org creator/joiner); employees (L2/L3) never do -- they
        // only get ProjectMembership rows -- so "does this caller genuinely belong to this org"
        // means different things per role. Checking IsMemberAsync for an L2/L3 caller would 403
        // every one of them, since that row can't exist for an employee.
        var callerId = this.CurrentUserId();
        var callerBelongsToOrg = role == AppRole.Admin
            ? await _memberships.IsMemberAsync(callerId, orgId, ct)
            : (await _projectMemberships.GetProjectIdsForUserAsync(callerId, ct)).Any(orgProjectIds.Contains);
        if (!callerBelongsToOrg) return Forbid();

        var allUsers = await _users.GetAllAsync(ct);
        var result = new List<EmployeeSummary>();
        foreach (var user in allUsers)
        {
            var userProjectIds = (await _projectMemberships.GetProjectIdsForUserAsync(user.Id, ct))
                .Where(orgProjectIds.Contains).ToList();
            if (userProjectIds.Count > 0)
                result.Add(new EmployeeSummary(user.Id, user.UserName, user.Role, userProjectIds));
        }
        return Ok(result);
    }

    // U4/U7: Admin-gated employee registration -- no invite-token flow, the Admin sets the
    // password directly and shares it out of band. Two-tier with AuthController.Register: this
    // endpoint never creates an org or auto-joins one, it only grants access to projects the Admin
    // explicitly lists, all of which must already belong to the Admin's own org.
    [HttpPost("{orgId}/employees")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<EmployeeSummary>> RegisterEmployee(
        string orgId, [FromBody] RegisterEmployeeRequest request, CancellationToken ct = default)
    {
        if (this.CurrentUserRole() != AppRole.Admin) return Forbid();
        var adminId = this.CurrentUserId();
        if (!await _memberships.IsMemberAsync(adminId, orgId, ct)) return Forbid();

        foreach (var projectId in request.ProjectIds)
        {
            var project = await _projects.GetByIdAsync(projectId, ct);
            if (project is null || project.OrgId != orgId)
                return BadRequest($"Project '{projectId}' does not belong to org '{orgId}'.");
        }

        if (await _userManager.FindByNameAsync(request.UserName) is not null)
            return Conflict("Username is already taken.");

        var user = new AppUser { Id = Guid.NewGuid().ToString("n"), UserName = request.UserName, Role = request.Role };
        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        foreach (var projectId in request.ProjectIds)
            await _projectMemberships.AddAsync(user.Id, projectId, ct);

        // U7: audit log -- actor, target, and the exact role/project grant, at the one call site
        // that currently changes role/project access. Matches the {Field}={Value} structured-message
        // pattern ChatController's TokenUsage/Agent logs already use.
        _logger.LogInformation(
            "EmployeeRegistered actor={ActorId} target={TargetId} org={OrgId} role={Role} projects={ProjectIds}",
            adminId, user.Id, orgId, request.Role, string.Join(",", request.ProjectIds));

        return Ok(new EmployeeSummary(user.Id, user.UserName, user.Role, request.ProjectIds));
    }
}
