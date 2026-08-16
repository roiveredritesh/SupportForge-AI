using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
    private readonly UserManager<AppUser> _userManager;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _users = new JsonFileUserRepository(_tempDir);
        _orgs = new JsonFileOrgRepository(_tempDir);
        _orgMemberships = new JsonFileOrgMembershipRepository(_tempDir);
        _userManager = OrgsControllerTests.MakeUserManager(_users);
        _controller = new AuthController(_userManager, new ConfigurationBuilder().Build(), _orgs, _orgMemberships);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static AuthController.RegisterRequest MakeRequest(
        string userName = "alice", string orgName = "Acme Inc", string? address = "123 Main St") =>
        new(orgName, userName, "Passw0rd!", "Jane Doe", "555-0100", "Software", address);

    [Fact]
    public async Task Register_NewUser_CreatesOrgAndMakesUserItsAdmin()
    {
        var result = await _controller.Register(MakeRequest());

        Assert.IsType<OkObjectResult>(result.Result);

        var allUsers = await _users.GetAllAsync();
        var user = Assert.Single(allUsers);
        Assert.Equal(AppRole.Admin, user.Role);

        var orgIds = await _orgMemberships.GetOrgIdsForUserAsync(user.Id);
        var orgId = Assert.Single(orgIds);
        var org = await _orgs.GetByIdAsync(orgId);
        Assert.NotNull(org);
        Assert.Equal("Acme Inc", org!.Name);
        Assert.Equal("Jane Doe", org.ContactPerson);
        Assert.Equal("555-0100", org.ContactNumber);
        Assert.Equal("Software", org.Industry);
        Assert.Equal("123 Main St", org.Address);
        Assert.True(await _orgMemberships.IsMemberAsync(user.Id, orgId));
    }

    [Fact]
    public async Task Register_AddressOmitted_OrgAddressIsNull()
    {
        var result = await _controller.Register(MakeRequest(address: null));

        Assert.IsType<OkObjectResult>(result.Result);

        var allOrgs = await _orgs.GetAllAsync();
        var org = Assert.Single(allOrgs);
        Assert.Null(org.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_MissingContactPerson_ReturnsBadRequestAndCreatesNothing(string blank)
    {
        var result = await _controller.Register(MakeRequest() with { ContactPerson = blank });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await _users.GetAllAsync());
        Assert.Empty(await _orgs.GetAllAsync());
    }

    [Fact]
    public async Task Register_MissingContactNumber_ReturnsBadRequestAndCreatesNothing()
    {
        var result = await _controller.Register(MakeRequest() with { ContactNumber = "" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await _users.GetAllAsync());
        Assert.Empty(await _orgs.GetAllAsync());
    }

    [Fact]
    public async Task Register_MissingIndustry_ReturnsBadRequestAndCreatesNothing()
    {
        var result = await _controller.Register(MakeRequest() with { Industry = "" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await _users.GetAllAsync());
        Assert.Empty(await _orgs.GetAllAsync());
    }

    [Fact]
    public async Task Register_DuplicateUserName_ReturnsConflict()
    {
        await _controller.Register(MakeRequest());

        var result = await _controller.Register(MakeRequest(orgName: "Different Org"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Register_TwoUsers_EachGetsOwnSeparateOrg()
    {
        await _controller.Register(MakeRequest(userName: "alice"));
        await _controller.Register(MakeRequest(userName: "carol"));

        var allOrgs = await _orgs.GetAllAsync();
        Assert.Equal(2, allOrgs.Count);
    }

    // U9: self-service change password.
    private async Task<AppUser> RegisterAndGetUser(string userName = "alice")
    {
        await _controller.Register(MakeRequest(userName: userName));
        return Assert.Single(await _users.GetAllAsync());
    }

    private void SetAsCurrentUser(AppUser user) =>
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) }, "TestAuth")),
            },
        };

    [Fact]
    public async Task ChangePassword_CorrectCurrentAndValidNew_SucceedsAndNewPasswordWorks()
    {
        var user = await RegisterAndGetUser();
        SetAsCurrentUser(user);

        var result = await _controller.ChangePassword(new AuthController.ChangePasswordRequest("Passw0rd!", "NewPassw0rd!"));

        Assert.IsType<OkResult>(result);
        var reloaded = await _users.GetByIdAsync(user.Id);
        Assert.NotNull(reloaded);
        Assert.True(await _userManager.CheckPasswordAsync(reloaded!, "NewPassw0rd!"));
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsBadRequestWithMismatchError()
    {
        var user = await RegisterAndGetUser();
        SetAsCurrentUser(user);

        var result = await _controller.ChangePassword(new AuthController.ChangePasswordRequest("WrongPassword!", "NewPassw0rd!"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var errors = Assert.IsAssignableFrom<IEnumerable<string>>(badRequest.Value);
        Assert.Contains(errors, e => e.Contains("incorrect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChangePassword_NewPasswordFailsStrengthRules_ReturnsBadRequestWithValidatorError()
    {
        var user = await RegisterAndGetUser();
        SetAsCurrentUser(user);

        var result = await _controller.ChangePassword(new AuthController.ChangePasswordRequest("Passw0rd!", "short"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ChangePassword_ThenLogin_NewPasswordWorksAndOldPasswordRejected()
    {
        var user = await RegisterAndGetUser();
        SetAsCurrentUser(user);
        await _controller.ChangePassword(new AuthController.ChangePasswordRequest("Passw0rd!", "NewPassw0rd!"));

        var loginWithNew = await _controller.Token(new AuthController.TokenRequest("alice", "NewPassw0rd!"));
        Assert.IsType<OkObjectResult>(loginWithNew.Result);

        var loginWithOld = await _controller.Token(new AuthController.TokenRequest("alice", "Passw0rd!"));
        Assert.IsType<UnauthorizedResult>(loginWithOld.Result);
    }
}
