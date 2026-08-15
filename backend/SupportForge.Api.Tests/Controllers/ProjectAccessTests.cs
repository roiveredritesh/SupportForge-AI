using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// B1 (gap-closing-solutions.md Phase B1): the Critical cross-project data-leak fix -- ProjectId is
// client-supplied on every request, so these tests assert the membership guard actually denies a
// non-member and that project creation auto-grants the creator, rather than just checking the wiring
// compiles.
public class ProjectAccessTests
{
    private static ClaimsPrincipal UserPrincipal(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth"));

    private static ProjectsController MakeProjectsController(string tempDir, string userId, out IProjectMembershipRepository memberships)
    {
        memberships = new JsonFileProjectMembershipRepository(tempDir);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(tempDir);
        var controller = new ProjectsController(
            new JsonFileProjectRepository(tempDir),
            memberships,
            new JsonFileOrgMembershipRepository(tempDir),
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            new Mock<IDeadLetterRepository>().Object,
            new Mock<IContentHashRepository>().Object,
            new Mock<IEscalationRepository>().Object,
            env.Object,
            new IngestionQueue(),
            Mock.Of<ILogger<ProjectsController>>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserPrincipal(userId) } };
        return controller;
    }

    [Fact]
    public async Task CreateProject_AutoGrantsMembershipToCreator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var controller = MakeProjectsController(tempDir, "alice", out var memberships);

        await controller.CreateOrUpdate(new Project { Id = "proj1", Name = "Alice's Project" });

        Assert.True(await memberships.IsMemberAsync("alice", "proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetAll_OnlyReturnsCallersOwnProjects_NotOtherUsersProjects()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var aliceController = MakeProjectsController(tempDir, "alice", out _);
        await aliceController.CreateOrUpdate(new Project { Id = "proj-alice", Name = "Alice's Project" });

        var bobController = MakeProjectsController(tempDir, "bob", out _);
        await bobController.CreateOrUpdate(new Project { Id = "proj-bob", Name = "Bob's Project" });

        var bobList = await bobController.GetAll();
        var bobOk = Assert.IsType<OkObjectResult>(bobList.Result);
        var bobProjects = Assert.IsAssignableFrom<IReadOnlyList<Project>>(bobOk.Value);

        Assert.Single(bobProjects);
        Assert.Equal("proj-bob", bobProjects[0].Id);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task NonMember_CannotUpdateAnotherUsersExistingProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var aliceController = MakeProjectsController(tempDir, "alice", out _);
        await aliceController.CreateOrUpdate(new Project { Id = "proj-alice", Name = "Original Name" });

        var bobController = MakeProjectsController(tempDir, "bob", out _);
        var result = await bobController.CreateOrUpdate(new Project { Id = "proj-alice", Name = "Hijacked Name" });

        Assert.IsType<ForbidResult>(result.Result);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task NonMember_CannotDeleteAnotherUsersProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var aliceController = MakeProjectsController(tempDir, "alice", out var memberships);
        await aliceController.CreateOrUpdate(new Project { Id = "proj-alice", Name = "Alice's Project" });

        var bobController = MakeProjectsController(tempDir, "bob", out _);
        var result = await bobController.Delete("proj-alice");

        Assert.IsType<ForbidResult>(result);
        Assert.True(await memberships.IsMemberAsync("alice", "proj-alice")); // untouched
        Directory.Delete(tempDir, recursive: true);
    }

}
