using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportForge.Api.Contracts;
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
    private readonly JsonFileTokenUsageRepository _tokenUsage;
    private readonly OrgsController _controller;

    public OrgsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _projects = new JsonFileProjectRepository(_tempDir);
        _projectMemberships = new JsonFileProjectMembershipRepository(_tempDir);
        _users = new JsonFileUserRepository(_tempDir);
        _userManager = MakeUserManager(_users);
        _tokenUsage = new JsonFileTokenUsageRepository(_tempDir);
        _controller = new OrgsController(
            new JsonFileOrgRepository(_tempDir),
            new JsonFileOrgMembershipRepository(_tempDir),
            _projects,
            _projectMemberships,
            _users,
            _userManager,
            _tokenUsage,
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
        var org = new Org { Id = "org1", Name = "Acme", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" };

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
        await _controller.CreateOrUpdate(new Org { Id = "org1", Name = "First User's Org", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });

        SetUser("second-user");
        await _controller.CreateOrUpdate(new Org { Id = "org2", Name = "Second User's Org", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });
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
        await _controller.CreateOrUpdate(new Org { Id = orgId, Name = "Acme", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });
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
        // An L2/L3 caller never gets an OrgMembership row (only the org's Admin does) -- their
        // ProjectMembership on one of the org's projects is what proves they belong to it.
        await _projectMemberships.AddAsync("test-user", "proj1");
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

    // An L2/L3 caller with no ProjectMembership on any of the target org's projects must still be
    // rejected -- role alone isn't enough, they must actually belong to that org.
    [Fact]
    public async Task GetEmployees_L3CallerNotMemberOfOrg_ReturnsForbid()
    {
        var orgId = await SeedOrgWithProjectAsync("org1", "proj1");
        SetUser("test-user", AppRole.L3);

        var result = await _controller.GetEmployees(orgId);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // U6: org-wide token usage endpoint.
    private async Task SeedTokenUsageAsync(params (string ProjectId, int Tokens, string? UserId, string Source)[] entries)
    {
        foreach (var (projectId, tokens, userId, source) in entries)
            await _tokenUsage.AddAsync(new TokenUsageEntry(projectId, tokens, DateTimeOffset.UtcNow, source, UserId: userId));
    }

    [Fact]
    public async Task GetOrgTokenUsage_ReturnsEntriesAcrossAllOfOrgsProjects()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _projects.UpsertAsync(new Project { Id = "proj2", Name = "Proj2", OrgId = "org1" });
        await SeedTokenUsageAsync(("proj1", 100, "alice", "chat"), ("proj2", 50, "bob", "chat"), ("proj-other-org", 999, "eve", "chat"));

        var result = await _controller.GetOrgTokenUsage("org1", null, null, null, null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var usage = Assert.IsType<OrgTokenUsage>(ok.Value);
        Assert.Equal(2, usage.Entries.Count);
        Assert.Contains(usage.Entries, e => e.ProjectId == "proj1" && e.TotalTokens == 100);
        Assert.Contains(usage.Entries, e => e.ProjectId == "proj2" && e.TotalTokens == 50);
    }

    // Covers AE5: userId filter narrows results org-wide, not per-project.
    [Fact]
    public async Task GetOrgTokenUsage_UserIdFilter_ReturnsOnlyThatUsersEntriesAcrossProjects()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _projects.UpsertAsync(new Project { Id = "proj2", Name = "Proj2", OrgId = "org1" });
        await SeedTokenUsageAsync(("proj1", 100, "alice", "chat"), ("proj2", 50, "alice", "chat"), ("proj1", 20, "bob", "chat"));

        var result = await _controller.GetOrgTokenUsage("org1", null, null, null, "alice");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var usage = Assert.IsType<OrgTokenUsage>(ok.Value);
        Assert.Equal(2, usage.Entries.Count);
        Assert.All(usage.Entries, e => Assert.Equal("alice", e.UserId));
    }

    [Fact]
    public async Task GetOrgTokenUsage_NonAdminCaller_ReturnsForbid()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        SetUser("test-user", AppRole.L1);

        var result = await _controller.GetOrgTokenUsage("org1", null, null, null, null);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // Covers KTD9: an Admin who belongs to a different org must not see this org's usage.
    [Fact]
    public async Task GetOrgTokenUsage_AdminOfDifferentOrg_ReturnsForbid()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        SetUser("second-admin", AppRole.Admin);
        await _controller.CreateOrUpdate(new Org { Id = "org2", Name = "Other Org", ContactPerson = "Jane Doe", ContactNumber = "555-0100", Industry = "Software" });

        var result = await _controller.GetOrgTokenUsage("org1", null, null, null, null);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetOrgTokenUsage_GridEntriesExcludeConfig_AndIncludeDistinctProjectIds()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _projects.UpsertAsync(new Project { Id = "proj2", Name = "Proj2", OrgId = "org1" });
        await _tokenUsage.AddAsync(new TokenUsageEntry(
            "proj1", 100, DateTimeOffset.UtcNow, "chat", Config: new Dictionary<string, string> { ["secret"] = "value" }));
        await _tokenUsage.AddAsync(new TokenUsageEntry("proj2", 50, DateTimeOffset.UtcNow, "chat"));

        var result = await _controller.GetOrgTokenUsage("org1", null, null, null, null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var usage = Assert.IsType<OrgTokenUsage>(ok.Value);
        // OrgTokenUsageEntry has no Config property at all -- compile-time proof it's excluded.
        Assert.Equal(new[] { "ProjectId", "UserId", "TotalTokens", "CreatedAt", "Source" },
            typeof(OrgsController).Assembly.GetType("SupportForge.Api.Contracts.OrgTokenUsageEntry")!
                .GetProperties().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "proj1", "proj2" }, usage.ProjectIds.OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task GetOrgTokenUsage_DayRangeFilter_NarrowsResults_DefaultsToLast30Days()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _tokenUsage.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow.AddDays(-2), "chat"));
        await _tokenUsage.AddAsync(new TokenUsageEntry("proj1", 999, DateTimeOffset.UtcNow.AddDays(-45), "chat"));

        var defaultResult = await _controller.GetOrgTokenUsage("org1", null, null, null, null);
        var defaultOk = Assert.IsType<OkObjectResult>(defaultResult.Result);
        var defaultUsage = Assert.IsType<OrgTokenUsage>(defaultOk.Value);
        Assert.Single(defaultUsage.Entries);
        Assert.Equal(100, defaultUsage.Entries[0].TotalTokens);

        var narrowedResult = await _controller.GetOrgTokenUsage(
            "org1", DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow, null, null);
        var narrowedOk = Assert.IsType<OkObjectResult>(narrowedResult.Result);
        var narrowedUsage = Assert.IsType<OrgTokenUsage>(narrowedOk.Value);
        Assert.Equal(2, narrowedUsage.Entries.Count);
    }

    [Fact]
    public async Task GetOrgTokenUsage_ProjectIdFilter_ScopedToOrg_RejectsOtherOrgsProject()
    {
        await SeedOrgWithProjectAsync("org1", "proj1");
        await _projects.UpsertAsync(new Project { Id = "proj2", Name = "Proj2", OrgId = "org1" });
        await _projects.UpsertAsync(new Project { Id = "other-org-proj", Name = "Other", OrgId = "org2" });
        await SeedTokenUsageAsync(("proj1", 100, "alice", "chat"), ("proj2", 50, "bob", "chat"), ("other-org-proj", 999, "eve", "chat"));

        var scoped = await _controller.GetOrgTokenUsage("org1", null, null, "proj1", null);
        var scopedOk = Assert.IsType<OkObjectResult>(scoped.Result);
        var scopedUsage = Assert.IsType<OrgTokenUsage>(scopedOk.Value);
        Assert.Single(scopedUsage.Entries);
        Assert.Equal("proj1", scopedUsage.Entries[0].ProjectId);

        // "other-org-proj" doesn't belong to org1, so it can never contribute entries regardless of
        // the projectId filter passed -- GetEntriesForProjectsAsync is only ever called with org1's
        // own project IDs.
        var otherOrgProject = await _controller.GetOrgTokenUsage("org1", null, null, "other-org-proj", null);
        var otherOk = Assert.IsType<OkObjectResult>(otherOrgProject.Result);
        var otherUsage = Assert.IsType<OrgTokenUsage>(otherOk.Value);
        Assert.Empty(otherUsage.Entries);
    }
}
