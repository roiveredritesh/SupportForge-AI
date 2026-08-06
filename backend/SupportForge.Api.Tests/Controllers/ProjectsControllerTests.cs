using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SupportForge.Api.Controllers;
using SupportForge.Api.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ProjectsControllerTests
{
    // B1: real (not mocked) file-backed membership repo, same pattern as the real JsonFileProjectRepository
    // these tests already use -- CreateOrUpdate/GetAll/Delete's membership logic needs actual persistence to
    // exercise the auto-grant-on-create -> GetAll-filters-by-membership round trip these tests assert on.
    private static ClaimsPrincipal TestUser(string userId = "test-user") =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth"));

    private static void SetTestUser(ProjectsController controller, string userId = "test-user") =>
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestUser(userId) } };

    [Fact]
    public async Task CreateProject_Then_ListProjects_ReturnsCreatedProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var memberships = new JsonFileProjectMembershipRepository(tempDir);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(tempDir);
        var controller = new ProjectsController(
            repo,
            memberships,
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            env.Object,
            new IngestionQueue(),
            Mock.Of<ILogger<ProjectsController>>());
        SetTestUser(controller);

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
            new JsonFileProjectMembershipRepository(tempDir),
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            env.Object,
            new IngestionQueue(),
            Mock.Of<ILogger<ProjectsController>>());
        SetTestUser(controller);

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
    public async Task Delete_WaitsForInFlightIngestionJobBeforeDeletingRepoDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(tempDir);
        var queue = new IngestionQueue();
        var controller = new ProjectsController(
            repo,
            new JsonFileProjectMembershipRepository(tempDir),
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            env.Object,
            queue,
            Mock.Of<ILogger<ProjectsController>>());
        SetTestUser(controller);

        var project = new Project { Id = "proj-ingesting", Name = "Ingesting Project" };
        await controller.CreateOrUpdate(project);

        var repoDir = Path.Combine(tempDir, "App_Data", "repos", project.Id);
        Directory.CreateDirectory(repoDir);
        var fileStillBeingRead = Path.Combine(repoDir, "readme.md");
        await File.WriteAllTextAsync(fileStillBeingRead, "hello");

        // Simulate an ingestion job for this project still running on the background worker:
        // the file is exclusively locked while "busy" is set, then released and marked
        // complete shortly after (mirroring IngestionBackgroundService's finally block).
        queue.Enqueue(new FakeJob(project.Id));
        var handle = new FileStream(fileStillBeingRead, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            handle.Dispose();
            queue.MarkComplete(project.Id);
        });

        var result = await controller.Delete(project.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(Directory.Exists(repoDir));

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Delete_StillRemovesProjectRecord_WhenRepoDirIsPermanentlyLocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(tempDir);
        var controller = new ProjectsController(
            repo,
            new JsonFileProjectMembershipRepository(tempDir),
            new Mock<IVectorStoreService>().Object,
            new Mock<IFeedbackRepository>().Object,
            new Mock<ITokenUsageRepository>().Object,
            new Mock<IConversationRepository>().Object,
            env.Object,
            new IngestionQueue(),
            Mock.Of<ILogger<ProjectsController>>());
        SetTestUser(controller);

        var project = new Project { Id = "proj-stuck-lock", Name = "Stuck Lock Project" };
        await controller.CreateOrUpdate(project);

        var repoDir = Path.Combine(tempDir, "App_Data", "repos", project.Id);
        Directory.CreateDirectory(repoDir);
        var lockedFile = Path.Combine(repoDir, "pack-fake.idx");
        await File.WriteAllTextAsync(lockedFile, "fake pack data");

        // Unlike the "momentarily locked" case above, this handle is never released -- simulates
        // a stuck OS-level lock (search indexer, AV) that outlives the bounded retry budget.
        using var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await controller.Delete(project.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await repo.GetByIdAsync(project.Id));
        Assert.True(Directory.Exists(repoDir)); // best-effort: left on disk, not blocking the record delete

        handle.Dispose();
        Directory.Delete(tempDir, recursive: true);
    }

    private sealed class FakeJob : SupportForge.Ingestion.IIngestionJob
    {
        public FakeJob(string projectId) => ProjectId = projectId;
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task PostProjects_WithStringKbSourceTypeEnum_ReturnsOk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // ProjectsController now requires [Authorize] (U4) -- isolate the user store from the
            // real App_Data and seed a test user rather than touch it.
            builder.ConfigureServices(services =>
                services.AddSingleton<IUserRepository>(new JsonFileUserRepository(tempDir)));
        });
        var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = "test-user", UserName = "tester" };
            var created = await userManager.CreateAsync(user, "Test-Password-123!");
            Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
        }

        var tokenResponse = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "tester", Password = "Test-Password-123!" });
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("accessToken").GetString();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
        {
            Content = JsonContent.Create(new
            {
                Id = "proj-with-kb",
                Name = "Project With KB",
                KbSources = new[] { new { Type = "Documents", Location = "docs/", LastSyncedAt = (DateTimeOffset?)null } },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 but got {(int)response.StatusCode}: {body}");

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }
}
