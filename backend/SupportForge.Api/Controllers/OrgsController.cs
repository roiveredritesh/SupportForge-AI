using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

// Sprint 0 (U3): mirrors ProjectsController's list/create-or-update shape and self-service
// authorization pattern. No Role field on AppUser yet -- creating an org just auto-joins the
// creator as a plain OrgMembership row, same as project creation does today.
[ApiController]
[Route("api/orgs")]
[Authorize]
public class OrgsController : ControllerBase
{
    private readonly IOrgRepository _repo;
    private readonly IOrgMembershipRepository _memberships;

    public OrgsController(IOrgRepository repo, IOrgMembershipRepository memberships)
    {
        _repo = repo;
        _memberships = memberships;
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
}
