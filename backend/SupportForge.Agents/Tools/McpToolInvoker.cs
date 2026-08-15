using ModelContextProtocol.Protocol;
using SupportForge.Core.Entities;

namespace SupportForge.Agents.Tools;

// Sprint 3 (U15): the single enforcement point an MCP tool call must go through -- static allowlist
// (does this connection permit the tool at all) then role gating (does the caller's role clear the
// tool's minimum), in that order, before ever reaching the MCP client. Both checks are cheap and
// local; only a call that passes both touches the network.
public sealed class McpAccessDeniedException : Exception
{
    public McpAccessDeniedException(string message) : base(message) { }
}

// Thin seam over the real MCP client SDK so allowlist/role-gating logic can be unit tested without
// spinning up a real MCP server. McpClientAdapter (below) is the production implementation backed
// by the actual ModelContextProtocol.Client.McpClient.
public interface IMcpToolClient
{
    Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);
}

public interface IMcpClientFactory
{
    Task<IMcpToolClient> GetClientAsync(McpConnection connection, CancellationToken ct);
}

public sealed class McpToolInvoker
{
    private readonly IMcpClientFactory _clientFactory;

    public McpToolInvoker(IMcpClientFactory clientFactory) => _clientFactory = clientFactory;

    public async Task<CallToolResult> InvokeAsync(
        McpConnection connection,
        string toolName,
        AppRole callerRole,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        // Layer 1: static allowlist -- the connection's own EnabledTools, set by an Admin at
        // connect time. Checked first and unconditionally: a tool outside the allowlist is
        // rejected no matter how privileged the caller is.
        if (!connection.EnabledTools.Contains(toolName))
            throw new McpAccessDeniedException($"Tool '{toolName}' is not enabled for this connection.");

        // Layer 2: role gating -- McpToolCatalog's per-tool minimum role.
        var minRole = McpToolCatalog.MinRoleFor(toolName);
        if (callerRole < minRole)
            throw new McpAccessDeniedException($"Tool '{toolName}' requires role '{minRole}' or higher.");

        var client = await _clientFactory.GetClientAsync(connection, ct);
        return await client.CallToolAsync(toolName, arguments, ct);
    }
}
