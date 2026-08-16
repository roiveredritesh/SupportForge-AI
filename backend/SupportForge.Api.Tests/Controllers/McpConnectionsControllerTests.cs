using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class McpConnectionsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileOrgRepository _orgs;
    private readonly JsonFileOrgMembershipRepository _memberships;
    private readonly McpConnectionsController _controller;

    public McpConnectionsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _orgs = new JsonFileOrgRepository(_tempDir);
        _memberships = new JsonFileOrgMembershipRepository(_tempDir);
        _controller = new McpConnectionsController(_orgs, _memberships);
        SetUser("admin-user");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void SetUser(string userId, AppRole role = AppRole.Admin) =>
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role.ToString()) }, "TestAuth")),
            },
        };

    private async Task SeedOrgAsync(string orgId, string adminUserId)
    {
        await _orgs.UpsertAsync(new Org { Id = orgId, Name = "Acme", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });
        await _memberships.AddAsync(adminUserId, orgId);
    }

    [Fact]
    public async Task Connect_AdminStoresConnection_RetrievableViaList()
    {
        await SeedOrgAsync("org1", "admin-user");

        var connectResult = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("github", "ghp_secret", new List<string> { "list_commits" }));

        var ok = Assert.IsType<OkObjectResult>(connectResult.Result);
        var summary = Assert.IsType<McpConnectionsController.ConnectionSummary>(ok.Value);
        Assert.Equal("github", summary.ServerType);
        Assert.Equal(new[] { "list_commits" }, summary.EnabledTools);

        var listResult = await _controller.GetConnections("org1");
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var connections = Assert.IsAssignableFrom<IReadOnlyList<McpConnectionsController.ConnectionSummary>>(listOk.Value);
        var stored = Assert.Single(connections);
        Assert.Equal("github", stored.ServerType);

        // Credential is never echoed back in the list response.
        var org = await _orgs.GetByIdAsync("org1");
        Assert.Equal("ghp_secret", org!.Connections.Single().Credential);
    }

    [Fact]
    public async Task Connect_NonAdminCaller_ReturnsForbid()
    {
        await SeedOrgAsync("org1", "admin-user");
        SetUser("other-user", AppRole.L1);

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("github", "ghp_secret", new List<string>()));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Connect_UnsupportedServerType_ReturnsBadRequest()
    {
        await SeedOrgAsync("org1", "admin-user");

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("jira", "token", new List<string>()));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Disconnect_AdminRemovesConnection()
    {
        await SeedOrgAsync("org1", "admin-user");
        await _controller.Connect("org1", new McpConnectionsController.ConnectRequest("github", "ghp_secret", new List<string> { "list_commits" }));

        var result = await _controller.Disconnect("org1", "github");

        Assert.IsType<NoContentResult>(result);
        var org = await _orgs.GetByIdAsync("org1");
        Assert.Empty(org!.Connections);
    }

    [Fact]
    public void GetCatalog_ListsGitHubServerType()
    {
        var result = _controller.GetCatalog();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var catalog = Assert.IsAssignableFrom<IReadOnlyList<McpConnectionsController.ServerCatalogEntry>>(ok.Value);
        var github = Assert.Single(catalog, c => c.ServerType == "github");
        Assert.NotEmpty(github.Tools);
    }
}
