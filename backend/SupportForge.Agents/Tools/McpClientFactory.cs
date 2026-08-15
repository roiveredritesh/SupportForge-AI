using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SupportForge.Core.Entities;

namespace SupportForge.Agents.Tools;

// Wraps a real ModelContextProtocol.Client.McpClient so McpToolInvoker only ever depends on the
// thin IMcpToolClient seam -- keeps the SDK type out of the invoker's testable surface.
internal sealed class McpClientAdapter : IMcpToolClient, IAsyncDisposable
{
    private readonly McpClient _client;
    public McpClientAdapter(McpClient client) => _client = client;

    public async Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct) =>
        await _client.CallToolAsync(toolName, new Dictionary<string, object?>(arguments), cancellationToken: ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

// Sprint 3 (U15): production IMcpClientFactory -- spawns the connection's MCP server over stdio.
// GitHub only this sprint (McpToolCatalog.ServerTools' only entry). ponytail: a fresh client/process
// per call, no pooling/reuse -- add caching if per-call spawn overhead matters.
public sealed class McpClientFactory : IMcpClientFactory
{
    public async Task<IMcpToolClient> GetClientAsync(McpConnection connection, CancellationToken ct)
    {
        var transport = connection.ServerType switch
        {
            "github" => BuildGitHubTransport(connection.Credential),
            _ => throw new NotSupportedException($"MCP server type '{connection.ServerType}' is not supported."),
        };

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        return new McpClientAdapter(client);
    }

    // ponytail: passes only the one env var the child process needs (this package version's
    // StdioClientTransportOptions has no curated-allowlist helper to build on top of) -- narrower
    // than inheriting the whole parent environment, which could otherwise leak other orgs' secrets
    // into the spawned server.
    private static StdioClientTransport BuildGitHubTransport(string token) =>
        new(new StdioClientTransportOptions
        {
            Name = "github",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-github"],
            EnvironmentVariables = new Dictionary<string, string?> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = token },
        });
}
