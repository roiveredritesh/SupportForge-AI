using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// U4: self-service registration -- new user + new Org + Admin membership, all in one call.
public class AuthControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileUserRepository _users;
    private readonly JsonFileOrgRepository _orgs;
    private readonly JsonFileOrgMembershipRepository _orgMemberships;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _users = new JsonFileUserRepository(_tempDir);
        _orgs = new JsonFileOrgRepository(_tempDir);
        _orgMemberships = new JsonFileOrgMembershipRepository(_tempDir);
        _controller = new AuthController(OrgsControllerTests.MakeUserManager(_users), new ConfigurationBuilder().Build(), _orgs, _orgMemberships);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Register_NewUser_CreatesOrgAndMakesUserItsAdmin()
    {
        var result = await _controller.Register(new AuthController.TokenRequest("alice", "Passw0rd!"));

        Assert.IsType<OkObjectResult>(result.Result);

        var allUsers = await _users.GetAllAsync();
        var user = Assert.Single(allUsers);
        Assert.Equal(AppRole.Admin, user.Role);

        var orgIds = await _orgMemberships.GetOrgIdsForUserAsync(user.Id);
        var orgId = Assert.Single(orgIds);
        Assert.NotNull(await _orgs.GetByIdAsync(orgId));
        Assert.True(await _orgMemberships.IsMemberAsync(user.Id, orgId));
    }

    [Fact]
    public async Task Register_DuplicateUserName_ReturnsConflict()
    {
        await _controller.Register(new AuthController.TokenRequest("alice", "Passw0rd!"));

        var result = await _controller.Register(new AuthController.TokenRequest("alice", "Different1!"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Register_TwoUsers_EachGetsOwnSeparateOrg()
    {
        await _controller.Register(new AuthController.TokenRequest("alice", "Passw0rd!"));
        await _controller.Register(new AuthController.TokenRequest("carol", "Passw0rd!"));

        var allOrgs = await _orgs.GetAllAsync();
        Assert.Equal(2, allOrgs.Count);
    }
}
