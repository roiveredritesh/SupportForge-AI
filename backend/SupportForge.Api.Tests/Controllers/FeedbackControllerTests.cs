using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SupportForge.Api.Controllers;
using SupportForge.Core;
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
        var controller = new FeedbackController(repo, memberships.Object);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") }, "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };

        var result = await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", true, false));

        Assert.IsType<OkResult>(result);
        Assert.True(File.Exists(Path.Combine(tempDir, "feedback.json")));

        Directory.Delete(tempDir, recursive: true);
    }
}
