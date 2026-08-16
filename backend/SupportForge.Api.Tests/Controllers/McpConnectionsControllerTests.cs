using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class McpConnectionsControllerTests : IDisposable
{
    // U7: fake HttpMessageHandler/IHttpClientFactory standing in for the GitHub `GET /user` probe,
    // mirroring CommitLookupToolTests' own FakeHttpMessageHandler pattern.
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public HttpClient CreateClient(string name) => new(new FakeHttpMessageHandler(_respond));
    }

    private static HttpResponseMessage GitHubUserResponse(string? expirationHeader = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"login": "octocat"}""", Encoding.UTF8, "application/json"),
        };
        if (expirationHeader is not null)
            response.Headers.Add("github-authentication-token-expiration", expirationHeader);
        return response;
    }

    private readonly string _tempDir;
    private readonly JsonFileOrgRepository _orgs;
    private readonly JsonFileOrgMembershipRepository _memberships;
    private McpConnectionsController _controller;

    public McpConnectionsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _orgs = new JsonFileOrgRepository(_tempDir);
        _memberships = new JsonFileOrgMembershipRepository(_tempDir);
        _controller = NewController(_ => GitHubUserResponse());
        SetUser("admin-user");
    }

    // Rebuilds the controller with a given fake GitHub response/failure, preserving the current user.
    private McpConnectionsController NewController(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var controller = new McpConnectionsController(_orgs, _memberships, new FakeHttpClientFactory(respond));
        if (_controller is not null) controller.ControllerContext = _controller.ControllerContext;
        return controller;
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

    // U7: GitHub token-expiry capture at connect-time.

    [Fact]
    public async Task Connect_GitHub_WhenExpirationHeaderPresent_StoresExpiresAt()
    {
        await SeedOrgAsync("org1", "admin-user");
        _controller = NewController(_ => GitHubUserResponse("2026-08-22 00:00:00 UTC"));

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("github", "ghp_secret", new List<string>()));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<McpConnectionsController.ConnectionSummary>(ok.Value);
        Assert.NotNull(summary.ExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero), summary.ExpiresAt);

        var org = await _orgs.GetByIdAsync("org1");
        Assert.Equal(summary.ExpiresAt, org!.Connections.Single().ExpiresAt);
    }

    [Fact]
    public async Task Connect_GitHub_WhenExpirationHeaderAbsent_SavesSuccessfully_ExpiresAtNull()
    {
        await SeedOrgAsync("org1", "admin-user");
        _controller = NewController(_ => GitHubUserResponse()); // fine-grained PAT: no header

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("github", "ghp_fine_grained", new List<string>()));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<McpConnectionsController.ConnectionSummary>(ok.Value);
        Assert.Null(summary.ExpiresAt);
    }

    [Fact]
    public async Task Connect_GitHub_WhenProbeCallFails_StillSavesConnection_ExpiresAtNull()
    {
        await SeedOrgAsync("org1", "admin-user");
        _controller = NewController(_ => throw new HttpRequestException("network unreachable"));

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("github", "ghp_bad", new List<string> { "list_commits" }));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<McpConnectionsController.ConnectionSummary>(ok.Value);
        Assert.Null(summary.ExpiresAt);

        var org = await _orgs.GetByIdAsync("org1");
        Assert.Single(org!.Connections); // connection still saved despite the probe failure
    }

    [Fact]
    public async Task Connect_GitHub_WhenProbeReturnsNon2xx_StillSavesConnection_ExpiresAtNull()
    {
        await SeedOrgAsync("org1", "admin-user");
        _controller = NewController(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("github", "ghp_bad", new List<string>()));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<McpConnectionsController.ConnectionSummary>(ok.Value);
        Assert.Null(summary.ExpiresAt);
    }

    [Fact]
    public async Task Connect_NonGitHubServer_SkipsProbe_ExpiresAtNull()
    {
        await SeedOrgAsync("org1", "admin-user");
        var called = false;
        _controller = NewController(_ => { called = true; return GitHubUserResponse("2026-08-22 00:00:00 UTC"); });

        var result = await _controller.Connect(
            "org1", new McpConnectionsController.ConnectRequest("jira", "token", new List<string>()));

        // "jira" is unsupported by the catalog, so this returns BadRequest before any probe -- confirms
        // the probe is gated on ServerType == "github", not fired for arbitrary connect calls.
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(called);
    }
}
