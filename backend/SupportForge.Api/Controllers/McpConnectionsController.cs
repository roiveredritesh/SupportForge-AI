using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Agents.Tools;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

// Sprint 3 (U14): Admin-facing CRUD for an org's MCP connections. Mirrors OrgsController's
// employee-registration endpoints for the [Authorize(Roles="Admin")] + org-scoping pattern
// (attribute-level gate as the real enforcement, explicit CurrentUserRole()/IsMemberAsync checks
// so the same "non-Admin -> 403" holds when an action is invoked directly, e.g. in a unit test).
[ApiController]
[Route("api/orgs/{orgId}/mcp-connections")]
[Authorize(Roles = "Admin")]
public class McpConnectionsController : ControllerBase
{
    private readonly IOrgRepository _orgs;
    private readonly IOrgMembershipRepository _memberships;

    public McpConnectionsController(IOrgRepository orgs, IOrgMembershipRepository memberships)
    {
        _orgs = orgs;
        _memberships = memberships;
    }

    public sealed record ToolCatalogEntry(string Name, string Description, AppRole MinRole);
    public sealed record ServerCatalogEntry(string ServerType, IReadOnlyList<ToolCatalogEntry> Tools);
    public sealed record ConnectionSummary(string ServerType, IReadOnlyList<string> EnabledTools);
    public sealed record ConnectRequest(string ServerType, string Credential, List<string> EnabledTools);

    // Lists supported MCP server types and their tools -- GitHub only this sprint. Static, not
    // org-scoped, but kept under the org-scoped route (rather than a top-level endpoint) since it's
    // only ever consumed from the same "Connected Apps" admin screen as the rest of this controller.
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<ServerCatalogEntry>> GetCatalog() =>
        Ok(McpToolCatalog.ServerTools.Select(kv =>
            new ServerCatalogEntry(kv.Key, kv.Value.Select(t => new ToolCatalogEntry(t.Name, t.Description, t.MinRole)).ToList())).ToList());

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConnectionSummary>>> GetConnections(string orgId, CancellationToken ct = default)
    {
        if (this.CurrentUserRole() != AppRole.Admin) return Forbid();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), orgId, ct)) return Forbid();

        var org = await _orgs.GetByIdAsync(orgId, ct);
        if (org is null) return NotFound();

        // Credential deliberately excluded from the response -- this list is for the admin UI's
        // "already connected" state and per-tool checkboxes, not for reading a stored PAT back out.
        return Ok(org.Connections.Select(c => new ConnectionSummary(c.ServerType, c.EnabledTools)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<ConnectionSummary>> Connect(string orgId, [FromBody] ConnectRequest request, CancellationToken ct = default)
    {
        if (this.CurrentUserRole() != AppRole.Admin) return Forbid();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), orgId, ct)) return Forbid();

        var org = await _orgs.GetByIdAsync(orgId, ct);
        if (org is null) return NotFound();

        if (!McpToolCatalog.ServerTools.ContainsKey(request.ServerType))
            return BadRequest($"Unsupported MCP server type '{request.ServerType}'.");

        var validTools = McpToolCatalog.ServerTools[request.ServerType].Select(t => t.Name).ToHashSet();
        var unknownTools = request.EnabledTools.Where(t => !validTools.Contains(t)).ToList();
        if (unknownTools.Count > 0)
            return BadRequest($"Unknown tool(s) for server type '{request.ServerType}': {string.Join(", ", unknownTools)}.");

        // Connect is also update -- reconnecting with a new credential/tool selection replaces the
        // existing connection for that server type rather than accumulating duplicates.
        org.Connections.RemoveAll(c => c.ServerType == request.ServerType);
        var connection = new McpConnection { ServerType = request.ServerType, Credential = request.Credential, EnabledTools = request.EnabledTools };
        org.Connections.Add(connection);
        await _orgs.UpsertAsync(org, ct);

        return Ok(new ConnectionSummary(connection.ServerType, connection.EnabledTools));
    }

    [HttpDelete("{serverType}")]
    public async Task<IActionResult> Disconnect(string orgId, string serverType, CancellationToken ct = default)
    {
        if (this.CurrentUserRole() != AppRole.Admin) return Forbid();
        if (!await _memberships.IsMemberAsync(this.CurrentUserId(), orgId, ct)) return Forbid();

        var org = await _orgs.GetByIdAsync(orgId, ct);
        if (org is null) return NotFound();

        org.Connections.RemoveAll(c => c.ServerType == serverType);
        await _orgs.UpsertAsync(org, ct);

        return NoContent();
    }
}
