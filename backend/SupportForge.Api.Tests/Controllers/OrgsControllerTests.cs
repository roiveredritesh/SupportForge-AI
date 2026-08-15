using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportForge.Api.Controllers;
using SupportForge.Api.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class OrgsControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileProjectRepository _projects;
    private readonly JsonFileProjectMembershipRepository _projectMemberships;
    private readonly JsonFileUserRepository _users;
    private readonly UserManager<AppUser> _userManager;
    private readonly OrgsController _controller;

    public OrgsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _projects = new JsonFileProjectRepository(_tempDir);
        _projectMemberships = new JsonFileProjectMembershipRepository(_tempDir);
        _users = new JsonFileUserRepository(_tempDir);
        _userManager = MakeUserManager(_users);
        _controller = new OrgsController(
            new JsonFileOrgRepository(_tempDir),
            new JsonFileOrgMembershipRepository(_tempDir),
            _projects,
            _projectMemberships,
            _users,
            _userManager,
            NullLogger<OrgsController>.Instance);
        SetUser("test-user");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Sprint 1 (U4/U7): a real UserManager<AppUser> around the same JSON-file-backed store
    // (CustomUserStore) Program.cs wires up -- OrgsController.RegisterEmployee needs it to actually
    // hash a password and create the user, not just a mock that always "succeeds".
    internal static UserManager<AppUser> MakeUserManager(IUserRepository repo) =>
        new(
            new CustomUserStore(repo),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            new List<IUserValidator<AppUser>> { new UserValidator<AppUser>() },
            new List<IPasswordValidator<AppUser>> { new PasswordValidator<AppUser>() },
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null,
            NullLogger<UserManager<AppUser>>.Instance);

    private void SetUser(string userId, AppRole role = AppRole.Admin) =>
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role.ToString()) }, "TestAuth")),
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

    // U4/U7: employee-registration scenarios.
    private async Task<string> SeedOrgWithProjectAsync(string orgId, string projectId)
    {
        await _controller.CreateOrUpdate(new Org { Id = orgId, Name = "Acme" });
        await _projects.UpsertAsync(new Project { Id = projectId, Name = "Proj", OrgId = orgId });
        return orgId;
    }

    [Fact]
    public async Task RegisterEmployee_AdminGrantsRoleAndProjects_CreatesUserWithExactMembershipRows()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _projects.UpsertAsync(new Project { Id = "proj2", Name = "Proj2", OrgId = "org1" });

        var result = await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Passw0rd!", AppRole.L2, new List<string> { "proj1", "proj2" }));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<OrgsController.EmployeeSummary>(ok.Value);
        Assert.Equal(AppRole.L2, summary.Role);
        Assert.Equal(2, summary.ProjectIds.Count);

        Assert.True(await _projectMemberships.IsMemberAsync(summary.Id, "proj1"));
        Assert.True(await _projectMemberships.IsMemberAsync(summary.Id, "proj2"));

        var created = await _users.GetByIdAsync(summary.Id);
        Assert.NotNull(created);
        Assert.Equal(AppRole.L2, created!.Role);
    }

    [Fact]
    public async Task RegisterEmployee_NonAdminCaller_ReturnsForbid()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        SetUser("test-user", AppRole.L1);

        var result = await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Passw0rd!", AppRole.L1, new List<string> { "proj1" }));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task RegisterEmployee_ProjectOutsideCallersOrg_Rejected()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _projects.UpsertAsync(new Project { Id = "other-org-proj", Name = "Other", OrgId = "org2" });

        var result = await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Passw0rd!", AppRole.L1, new List<string> { "other-org-proj" }));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RegisterEmployee_DuplicateUserName_ReturnsConflict()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Passw0rd!", AppRole.L1, new List<string> { "proj1" }));

        var result = await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Different1!", AppRole.L2, new List<string> { "proj1" }));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEmployees_ListsRegisteredEmployeesWithRoleAndProjectScope()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Passw0rd!", AppRole.L3, new List<string> { "proj1" }));

        var result = await _controller.GetEmployees("org1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var employees = Assert.IsAssignableFrom<IReadOnlyList<OrgsController.EmployeeSummary>>(ok.Value);
        var bob = Assert.Single(employees);
        Assert.Equal("bob", bob.UserName);
        Assert.Equal(AppRole.L3, bob.Role);
        Assert.Equal(new[] { "proj1" }, bob.ProjectIds);
    }

    // Regression: U27's "Invite Engineer" picker (ChatPage.tsx's useEmployees call) depends on this
    // endpoint, and L2/L3 -- not just Admin -- need to invite a colleague into a conversation. An
    // Admin-only gate here silently empties the picker for every L2/L3 caller (a 403 that
    // useEmployees swallows into "no options", not a visible error).
    [Theory]
    [InlineData(AppRole.L2)]
    [InlineData(AppRole.L3)]
    public async Task GetEmployees_L2OrL3Caller_Succeeds(AppRole role)
    {
        var orgId = await SeedOrgWithProjectAsync("org1", "proj1");
        await _controller.RegisterEmployee(
            "org1", new OrgsController.RegisterEmployeeRequest("bob", "Passw0rd!", AppRole.L1, new List<string> { "proj1" }));
        SetUser("test-user", role);

        var result = await _controller.GetEmployees(orgId);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetEmployees_L1Caller_ReturnsForbid()
    {
        var orgId = await SeedOrgWithProjectAsync("org1", "proj1");
        SetUser("test-user", AppRole.L1);

        var result = await _controller.GetEmployees(orgId);

        Assert.IsType<ForbidResult>(result.Result);
    }
}
