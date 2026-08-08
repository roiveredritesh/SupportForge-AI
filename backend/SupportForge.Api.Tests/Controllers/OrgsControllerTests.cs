using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// Mirrors ProjectAccessTests: asserts self-service org creation auto-grants the creator, and that
// a non-member can't silently take over another org's settings (including its PAT) by reusing its id.
public class OrgsControllerTests
{
    private static ClaimsPrincipal UserPrincipal(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth"));

    private static OrgsController MakeController(string tempDir, string userId, out IOrgMembershipRepository memberships)
    {
        memberships = new JsonFileOrgMembershipRepository(tempDir);
        var controller = new OrgsController(new JsonFileOrgRepository(tempDir), memberships);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserPrincipal(userId) } };
        return controller;
    }

    [Fact]
    public async Task CreateOrg_AutoGrantsMembershipToCreator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var controller = MakeController(tempDir, "alice", out var memberships);

        await controller.CreateOrUpdate(new Org { Id = "acme", Name = "Acme Corp", GitHubAccessToken = "ghp_test" });

        Assert.True(await memberships.IsMemberAsync("alice", "acme"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetAll_OnlyReturnsCallersOwnOrgs_NotOtherUsersOrgs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var aliceController = MakeController(tempDir, "alice", out _);
        await aliceController.CreateOrUpdate(new Org { Id = "org-alice", Name = "Alice Inc" });

        var bobController = MakeController(tempDir, "bob", out _);
        await bobController.CreateOrUpdate(new Org { Id = "org-bob", Name = "Bob Inc" });

        var bobList = await bobController.GetAll();
        var bobOk = Assert.IsType<OkObjectResult>(bobList.Result);
        var bobOrgs = Assert.IsAssignableFrom<IReadOnlyList<Org>>(bobOk.Value);

        Assert.Single(bobOrgs);
        Assert.Equal("org-bob", bobOrgs[0].Id);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task NonMember_CannotRotateAnotherOrgsToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var aliceController = MakeController(tempDir, "alice", out _);
        await aliceController.CreateOrUpdate(new Org { Id = "org-alice", Name = "Alice Inc", GitHubAccessToken = "original-token" });

        var bobController = MakeController(tempDir, "bob", out _);
        var result = await bobController.CreateOrUpdate(new Org { Id = "org-alice", Name = "Alice Inc", GitHubAccessToken = "stolen-token" });

        Assert.IsType<ForbidResult>(result.Result);
        Directory.Delete(tempDir, recursive: true);
    }
}
