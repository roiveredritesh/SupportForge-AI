using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ProjectsControllerTests
{
    [Fact]
    public async Task CreateProject_Then_ListProjects_ReturnsCreatedProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(tempDir);
        var controller = new ProjectsController(
            repo,
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            env.Object);

        var project = new Project { Id = "proj1", Name = "Test Project" };
        await controller.CreateOrUpdate(project);

        var result = await controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var projects = Assert.IsAssignableFrom<IReadOnlyList<Project>>(ok.Value);

        Assert.Single(projects);
        Assert.Equal("Test Project", projects[0].Name);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Delete_RetriesWhenRepoDirFileIsMomentarilyLocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(tempDir);
        var controller = new ProjectsController(
            repo,
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            env.Object);

        var project = new Project { Id = "proj-locked", Name = "Locked Repo Project" };
        await controller.CreateOrUpdate(project);

        var repoDir = Path.Combine(tempDir, "App_Data", "repos", project.Id);
        Directory.CreateDirectory(repoDir);
        var lockedFile = Path.Combine(repoDir, "pack-fake.idx");
        await File.WriteAllTextAsync(lockedFile, "fake pack data");

        // Simulate the libgit2 mmap-not-yet-released window: the file is exclusively
        // locked when Delete is first called, then released shortly after.
        var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            handle.Dispose();
        });

        var result = await controller.Delete(project.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(Directory.Exists(repoDir));

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task PostProjects_WithStringKbSourceTypeEnum_ReturnsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/projects", JsonContent.Create(new
        {
            Id = "proj-with-kb",
            Name = "Project With KB",
            KbSources = new[] { new { Type = "Documents", Location = "docs/", LastSyncedAt = (DateTimeOffset?)null } },
        }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 but got {(int)response.StatusCode}: {body}");
    }
}
