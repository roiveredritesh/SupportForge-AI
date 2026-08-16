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
    private readonly HttpClient _http;

    public McpConnectionsController(IOrgRepository orgs, IOrgMembershipRepository memberships, IHttpClientFactory httpClientFactory)
    {
        _orgs = orgs;
        _memberships = memberships;
        // Reuses the same named "GitHubApi" client CommitLookupTool registers with -- see Program.cs.
        _http = httpClientFactory.CreateClient("GitHubApi");
    }

    public sealed record ToolCatalogEntry(string Name, string Description, AppRole MinRole);
    public sealed record ServerCatalogEntry(string ServerType, IReadOnlyList<ToolCatalogEntry> Tools);
    public sealed record ConnectionSummary(string ServerType, IReadOnlyList<string> EnabledTools, DateTimeOffset? ExpiresAt);
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
        // ExpiresAt is not a credential, safe to return -- the frontend's TokenExpiryAlert (U7) does
        // its own 7-day-threshold check against this value, so no dedicated "expiring" endpoint.
        return Ok(org.Connections.Select(c => new ConnectionSummary(c.ServerType, c.EnabledTools, c.ExpiresAt)).ToList());
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

        // U7: best-effort GitHub PAT expiry probe -- never blocks or fails the connect flow. Bad
        // token / network failure / no header (fine-grained PAT) all just leave ExpiresAt null;
        // McpToolInvoker is what actually validates the credential works, at tool-use time.
        DateTimeOffset? expiresAt = request.ServerType == "github"
            ? await TryGetGitHubTokenExpiryAsync(request.Credential, ct)
            : null;

        // Connect is also update -- reconnecting with a new credential/tool selection replaces the
        // existing connection for that server type rather than accumulating duplicates.
        org.Connections.RemoveAll(c => c.ServerType == request.ServerType);
        var connection = new McpConnection
        {
            ServerType = request.ServerType,
            Credential = request.Credential,
            EnabledTools = request.EnabledTools,
            ExpiresAt = expiresAt,
        };
        org.Connections.Add(connection);
        await _orgs.UpsertAsync(org, ct);

        return Ok(new ConnectionSummary(connection.ServerType, connection.EnabledTools, connection.ExpiresAt));
    }

    // GET /user is the lightest authenticated GitHub REST call available -- only used to read the
    // `github-authentication-token-expiration` response header GitHub sets for classic PATs.
    // Mirrors CommitLookupTool's HttpClient/graceful-degradation pattern (Sprint 2).
    private async Task<DateTimeOffset?> TryGetGitHubTokenExpiryAsync(string credential, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Add("User-Agent", "SupportForge");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {credential}");

            using var response = await _http.SendAsync(request, ct);

            if (response.Headers.TryGetValues("github-authentication-token-expiration", out var values))
                return ParseGitHubExpiration(values.FirstOrDefault());

            return null;
        }
        catch
        {
            return null;
        }
    }

    // GitHub's header value looks like "2021-06-03 19:22:26 UTC" -- a trailing "UTC" name, not an
    // offset, which DateTimeOffset.TryParse doesn't understand on its own. Strip it and treat the
    // rest as UTC explicitly; any other/unexpected shape falls back to null rather than throwing.
    private static DateTimeOffset? ParseGitHubExpiration(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;

        var trimmed = headerValue.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase)
            ? headerValue[..^4]
            : headerValue;

        return DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
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
