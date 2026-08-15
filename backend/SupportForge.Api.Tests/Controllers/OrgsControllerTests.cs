using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class OrgsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OrgsController _controller;

    public OrgsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _controller = new OrgsController(
            new JsonFileOrgRepository(_tempDir),
            new JsonFileOrgMembershipRepository(_tempDir));
        SetUser("test-user");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void SetUser(string userId) =>
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth")),
            },
        };

    [Fact]
    public async Task GetAll_UserWithNoOrgs_ReturnsEmptyList()
    {
        var result = await _controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var orgs = Assert.IsAssignableFrom<IReadOnlyList<Org>>(ok.Value);
        Assert.Empty(orgs);
    }

    [Fact]
    public async Task CreateOrg_AutoJoinsCreator()
    {
        var org = new Org { Id = "org1", Name = "Acme" };

        await _controller.CreateOrUpdate(org);
        var result = await _controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var orgs = Assert.IsAssignableFrom<IReadOnlyList<Org>>(ok.Value);
        Assert.Single(orgs);
        Assert.Equal("Acme", orgs[0].Name);
    }

    [Fact]
    public async Task GetAll_SecondUsersOrgs_DoNotLeakIntoFirstUsersList()
    {
        await _controller.CreateOrUpdate(new Org { Id = "org1", Name = "First User's Org" });

        SetUser("second-user");
        await _controller.CreateOrUpdate(new Org { Id = "org2", Name = "Second User's Org" });
        var secondUserResult = await _controller.GetAll();

        SetUser("test-user");
        var firstUserResult = await _controller.GetAll();

        var secondOrgs = Assert.IsAssignableFrom<IReadOnlyList<Org>>(Assert.IsType<OkObjectResult>(secondUserResult.Result).Value);
        var firstOrgs = Assert.IsAssignableFrom<IReadOnlyList<Org>>(Assert.IsType<OkObjectResult>(firstUserResult.Result).Value);

        Assert.Single(firstOrgs);
        Assert.Equal("org1", firstOrgs[0].Id);
        Assert.Single(secondOrgs);
        Assert.Equal("org2", secondOrgs[0].Id);
    }
}
