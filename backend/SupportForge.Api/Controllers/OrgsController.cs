using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

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

    // Lists only the orgs the caller is a member of, same pattern as ProjectsController.GetAll.
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Org>>> GetAll(CancellationToken ct = default)
    {
        var memberOrgIds = await _memberships.GetOrgIdsForUserAsync(this.CurrentUserId(), ct);
        var all = await _repo.GetAllAsync(ct);
        return Ok(all.Where(o => memberOrgIds.Contains(o.Id)).ToList());
    }

    // Self-service create, same pattern as ProjectsController.CreateOrUpdate: creator auto-joins;
    // updating an existing org requires the caller already be a member. Also the endpoint for
    // setting/rotating the PAT (GitHubAccessToken on the posted body) -- no separate endpoint.
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
