namespace SupportForge.Core.Entities;

// Sprint 3 (U13): one connected MCP server on an Org. Credential is a plain string field -- no
// encryption infra here, same plaintext-for-now precedent as the existing global GitHub:Token
// config (see GitRepoSyncService). EnabledTools is the static allowlist half of McpToolInvoker's
// two-layer enforcement (allowlist + role gating, see SupportForge.Agents/Tools/McpToolInvoker.cs).
public sealed class McpConnection
{
    // e.g. "github" -- matches McpToolCatalog's ServerType keys.
    public required string ServerType { get; init; }
    public required string Credential { get; init; }
    public List<string> EnabledTools { get; init; } = new();
}
