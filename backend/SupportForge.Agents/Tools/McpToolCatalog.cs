using SupportForge.Core.Entities;

namespace SupportForge.Agents.Tools;

// Sprint 3 (U14/U15): what a connected MCP server's tools are, what they do, and the minimum role
// allowed to invoke each. Single source both McpConnectionsController (catalog + per-tool
// checkboxes/tooltips) and McpToolInvoker (role gating) read from, so admin UI and enforcement
// can't drift apart. GitHub only this sprint -- ponytail: hardcoded in-code table, not a
// database/config file; add a real registry when a second server type shows up.
public sealed record McpToolDescriptor(string Name, string Description, AppRole MinRole);

public static class McpToolCatalog
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<McpToolDescriptor>> ServerTools =
        new Dictionary<string, IReadOnlyList<McpToolDescriptor>>
        {
            ["github"] = new List<McpToolDescriptor>
            {
                new("list_commits", "Lists recent commits on a repo branch. Read-only.", AppRole.L1),
                new("get_pull_request", "Reads a single pull request's details. Read-only.", AppRole.L1),
                new("create_issue", "Opens a new GitHub issue on the repo.", AppRole.L2),
                new("merge_pull_request", "Merges a pull request -- writes to the repo.", AppRole.Admin),
            },
        };

    // Unrecognized tool names default to the strictest gate (Admin) rather than the loosest --
    // an unknown tool should never be easier to reach than a known one.
    public static AppRole MinRoleFor(string toolName) =>
        ServerTools.Values.SelectMany(t => t).FirstOrDefault(t => t.Name == toolName)?.MinRole ?? AppRole.Admin;
}
