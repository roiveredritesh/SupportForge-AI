using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// C7 (gap-closing-solutions.md Phase C, item 7): dead-letter visibility endpoints.
public class IngestionControllerTests
{
    private static ClaimsPrincipal UserPrincipal(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth"));

    private static (IngestionController controller, JsonFileProjectMembershipRepository memberships, JsonFileDeadLetterRepository deadLetters, string tempDir) MakeSut(string userId = "alice")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        var memberships = new JsonFileProjectMembershipRepository(tempDir);
        var deadLetters = new JsonFileDeadLetterRepository(tempDir);
        var services = new ServiceCollection().BuildServiceProvider();

        var controller = new IngestionController(new IngestionQueue(), projects, memberships, deadLetters, services);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserPrincipal(userId) } };
        return (controller, memberships, deadLetters, tempDir);
    }

    [Fact]
    public async Task GetDeadLetters_NonMember_ReturnsForbid()
    {
        var (controller, _, _, tempDir) = MakeSut();

        var result = await controller.GetDeadLetters("proj1", default);

        Assert.IsType<ForbidResult>(result.Result);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetDeadLetters_Member_ReturnsOnlyThatProjectsEntries()
    {
        var (controller, memberships, deadLetters, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");
        await deadLetters.AddAsync(new DeadLetterEntry("dl1", "proj1", "WebsiteIngestionJob", "boom", DateTimeOffset.UtcNow));
        await deadLetters.AddAsync(new DeadLetterEntry("dl2", "proj-other", "CodeIngestionJob", "boom2", DateTimeOffset.UtcNow));

        var result = await controller.GetDeadLetters("proj1", default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<DeadLetterEntry>>(ok.Value);
        var entry = Assert.Single(entries);
        Assert.Equal("dl1", entry.Id);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task DismissDeadLetter_NonMember_ReturnsForbid_AndDoesNotDelete()
    {
        var (controller, memberships, deadLetters, tempDir) = MakeSut(userId: "bob");
        await memberships.AddAsync("alice", "proj1"); // bob is not a member
        await deadLetters.AddAsync(new DeadLetterEntry("dl1", "proj1", "WebsiteIngestionJob", "boom", DateTimeOffset.UtcNow));

        var result = await controller.DismissDeadLetter("dl1", default);

        Assert.IsType<ForbidResult>(result);
        Assert.NotNull(await deadLetters.GetByIdAsync("dl1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task DismissDeadLetter_Member_DeletesEntry()
    {
        var (controller, memberships, deadLetters, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");
        await deadLetters.AddAsync(new DeadLetterEntry("dl1", "proj1", "WebsiteIngestionJob", "boom", DateTimeOffset.UtcNow));

        var result = await controller.DismissDeadLetter("dl1", default);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await deadLetters.GetByIdAsync("dl1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task DismissDeadLetter_UnknownId_ReturnsNotFound()
    {
        var (controller, _, _, tempDir) = MakeSut();

        var result = await controller.DismissDeadLetter("does-not-exist", default);

        Assert.IsType<NotFoundResult>(result);
        Directory.Delete(tempDir, recursive: true);
    }
}
