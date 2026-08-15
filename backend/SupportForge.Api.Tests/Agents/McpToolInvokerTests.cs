using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SupportForge.Agents.Tools;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// Sprint 3 (U15): McpToolInvoker's allowlist + role-gating enforcement, unit-tested against a fake
// IMcpClientFactory (no real MCP server involved -- these two checks are pure and local), plus one
// integration-style test that runs a real end-to-end call through the actual MCP client SDK against
// an in-memory MCP server (System.IO.Pipelines transport, per the SDK's own testing guidance --
// no live external MCP server needed).
public class McpToolInvokerTests
{
    private sealed class FakeMcpToolClient : IMcpToolClient
    {
        public string? LastToolName { get; private set; }
        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        {
            LastToolName = toolName;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] });
        }
    }

    private sealed class FakeMcpClientFactory : IMcpClientFactory
    {
        public FakeMcpToolClient Client { get; } = new();
        public Task<IMcpToolClient> GetClientAsync(McpConnection connection, CancellationToken ct) => Task.FromResult<IMcpToolClient>(Client);
    }

    private static McpConnection GitHubConnection(params string[] enabledTools) =>
        new() { ServerType = "github", Credential = "ghp_secret", EnabledTools = enabledTools.ToList() };

    [Fact]
    public async Task InvokeAsync_ToolNotInAllowlist_RejectedRegardlessOfRole()
    {
        var factory = new FakeMcpClientFactory();
        var invoker = new McpToolInvoker(factory);
        var connection = GitHubConnection(); // no tools enabled

        // Admin -- the most privileged role there is -- still gets rejected because the allowlist
        // check runs first and unconditionally.
        await Assert.ThrowsAsync<McpAccessDeniedException>(() =>
            invoker.InvokeAsync(connection, "list_commits", AppRole.Admin, new Dictionary<string, object?>()));

        Assert.Null(factory.Client.LastToolName);
    }

    [Fact]
    public async Task InvokeAsync_ToolAllowedButRoleTooLow_Rejected()
    {
        var factory = new FakeMcpClientFactory();
        var invoker = new McpToolInvoker(factory);
        // create_issue requires L2+ per McpToolCatalog.
        var connection = GitHubConnection("create_issue");

        await Assert.ThrowsAsync<McpAccessDeniedException>(() =>
            invoker.InvokeAsync(connection, "create_issue", AppRole.L1, new Dictionary<string, object?>()));

        Assert.Null(factory.Client.LastToolName);
    }

    [Fact]
    public async Task InvokeAsync_ToolAllowedAndRoleSufficient_InvokesClient()
    {
        var factory = new FakeMcpClientFactory();
        var invoker = new McpToolInvoker(factory);
        var connection = GitHubConnection("list_commits");

        var result = await invoker.InvokeAsync(connection, "list_commits", AppRole.L1, new Dictionary<string, object?>());

        Assert.Equal("list_commits", factory.Client.LastToolName);
        Assert.NotEqual(true, result.IsError);
    }

    // Integration-style: a real McpClient talking to a real (in-process, in-memory-transport)
    // McpServer, both from the actual ModelContextProtocol SDK -- no fakes below McpToolInvoker's
    // own IMcpClientFactory seam.
    [Fact]
    public async Task InvokeAsync_EndToEnd_AgainstInMemoryMcpServer_ReturnsToolResult()
    {
        Pipe clientToServer = new(), serverToClient = new();

        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ToolCollection = [McpServerTool.Create((string sha) => $"commit {sha}", new() { Name = "list_commits" })],
            });
        var serverRun = server.RunAsync();

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

        var factory = new SingleClientFactory(client);
        var invoker = new McpToolInvoker(factory);
        var connection = GitHubConnection("list_commits");

        var result = await invoker.InvokeAsync(
            connection, "list_commits", AppRole.L1, new Dictionary<string, object?> { ["sha"] = "abc123" });

        var text = Assert.Single(result.Content.OfType<TextContentBlock>());
        Assert.Equal("commit abc123", text.Text);

        await client.DisposeAsync();
        await server.DisposeAsync();
        try { await serverRun; } catch { /* transport torn down by disposal above */ }
    }

    private sealed class SingleClientFactory : IMcpClientFactory
    {
        private readonly McpClient _client;
        public SingleClientFactory(McpClient client) => _client = client;
        public Task<IMcpToolClient> GetClientAsync(McpConnection connection, CancellationToken ct) =>
            Task.FromResult<IMcpToolClient>(new PassthroughAdapter(_client));
    }

    // Local adapter (McpClientAdapter in SupportForge.Agents is internal) -- same shape, just calls
    // straight through to the already-connected McpClient this test built.
    private sealed class PassthroughAdapter : IMcpToolClient
    {
        private readonly McpClient _client;
        public PassthroughAdapter(McpClient client) => _client = client;
        public async Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct) =>
            await _client.CallToolAsync(toolName, new Dictionary<string, object?>(arguments), cancellationToken: ct);
    }
}
