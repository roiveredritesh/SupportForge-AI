using Microsoft.AspNetCore.Mvc;
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
        var controller = new FeedbackController(repo);

        var result = await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", true, false));

        Assert.IsType<OkResult>(result);
        Assert.True(File.Exists(Path.Combine(tempDir, "feedback.json")));

        Directory.Delete(tempDir, recursive: true);
    }
}
