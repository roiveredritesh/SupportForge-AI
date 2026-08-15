using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class FeedbackControllerTests
{
    [Fact]
    public async Task Submit_ReturnsOk_AndPersistsEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = new FeedbackController(repo, memberships.Object, new Mock<IOrgMembershipRepository>().Object, new Mock<IProjectRepository>().Object);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") }, "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };

        var result = await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", true, false));

        Assert.IsType<OkResult>(result);
        Assert.True(File.Exists(Path.Combine(tempDir, "feedback.json")));

        Directory.Delete(tempDir, recursive: true);
    }

    // U5: the submitting user's id is stamped onto the persisted entry.
    [Fact]
    public async Task Submit_StampsCallersUserId_OnPersistedEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = new FeedbackController(repo, memberships.Object, new Mock<IOrgMembershipRepository>().Object, new Mock<IProjectRepository>().Object);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "stamped-user") }, "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };

        await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", true, false));

        var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "feedback.json"));
        var entries = System.Text.Json.JsonSerializer.Deserialize<List<SupportForge.Core.Entities.FeedbackEntry>>(json)!;
        var entry = Assert.Single(entries);
        Assert.Equal("stamped-user", entry.UserId);

        Directory.Delete(tempDir, recursive: true);
    }

    // U17: "not useful" requires a reason code -- missing one is rejected before persisting.
    [Fact]
    public async Task Submit_NotUsefulWithoutReasonCode_ReturnsBadRequest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = new FeedbackController(repo, memberships.Object, new Mock<IOrgMembershipRepository>().Object, new Mock<IProjectRepository>().Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") }, "TestAuth")) },
        };

        var result = await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", false, false));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(File.Exists(Path.Combine(tempDir, "feedback.json")));

        Directory.Delete(tempDir, recursive: true);
    }

    // U17: reason code is optional/absent on "useful" feedback.
    [Fact]
    public async Task Submit_Useful_DoesNotRequireReasonCode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        var memberships = new Mock<IProjectMembershipRepository>();
        memberships.Setup(m => m.IsMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = new FeedbackController(repo, memberships.Object, new Mock<IOrgMembershipRepository>().Object, new Mock<IProjectRepository>().Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") }, "TestAuth")) },
        };

        var result = await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", true, false));

        Assert.IsType<OkResult>(result);
        Directory.Delete(tempDir, recursive: true);
    }

    // U18: Admin sees negative feedback + reason-code breakdown only for projects in their own org.
    [Fact]
    public async Task Dashboard_AdminSeesNegativeFeedback_OnlyForOwnOrgProjects()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        await repo.AddAsync(new FeedbackEntry("proj-mine", "q1", false, false, DateTimeOffset.UtcNow, "u1", null, FeedbackReasonCode.Incomplete));
        await repo.AddAsync(new FeedbackEntry("proj-mine", "q2", false, false, DateTimeOffset.UtcNow, "u2", null, FeedbackReasonCode.Incomplete));
        await repo.AddAsync(new FeedbackEntry("proj-other-org", "q3", false, false, DateTimeOffset.UtcNow, "u3", null, FeedbackReasonCode.Irrelevant));

        var memberships = new Mock<IProjectMembershipRepository>();
        var orgMemberships = new Mock<IOrgMembershipRepository>();
        orgMemberships.Setup(o => o.GetOrgIdsForUserAsync("admin-user", It.IsAny<CancellationToken>())).ReturnsAsync(new List<string> { "org-mine" });
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Project>
        {
            new() { Id = "proj-mine", Name = "Mine", OrgId = "org-mine" },
            new() { Id = "proj-other-org", Name = "Other", OrgId = "org-other" },
        });

        var controller = new FeedbackController(repo, memberships.Object, orgMemberships.Object, projects.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "admin-user"), new Claim(ClaimTypes.Role, "Admin") }, "TestAuth")),
            },
        };

        var response = await controller.Dashboard();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<FeedbackController.DashboardResponse>(ok.Value);
        Assert.Equal(2, body.RecentNegative.Count);
        Assert.All(body.RecentNegative, e => Assert.Equal("proj-mine", e.ProjectId));
        Assert.Equal(2, body.ReasonCodeBreakdown[nameof(FeedbackReasonCode.Incomplete)]);

        Directory.Delete(tempDir, recursive: true);
    }

    // U18: non-Admin caller gets 403.
    [Fact]
    public async Task Dashboard_NonAdminCaller_ReturnsForbid()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        var controller = new FeedbackController(
            repo, new Mock<IProjectMembershipRepository>().Object, new Mock<IOrgMembershipRepository>().Object, new Mock<IProjectRepository>().Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "l1-user"), new Claim(ClaimTypes.Role, "L1") }, "TestAuth")),
            },
        };

        var response = await controller.Dashboard();

        Assert.IsType<ForbidResult>(response.Result);
        Directory.Delete(tempDir, recursive: true);
    }
}
