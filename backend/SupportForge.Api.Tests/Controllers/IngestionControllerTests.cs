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

    private sealed class RecordingJobFactory : IIngestionJobFactory
    {
        public List<Project> CallsWithProjectSnapshot { get; } = new();
        public List<string?> TriggeredByUserIds { get; } = new();
        public IEnumerable<IIngestionJob> CreateJobs(Project project, string? triggeredByUserId)
        {
            CallsWithProjectSnapshot.Add(project);
            TriggeredByUserIds.Add(triggeredByUserId);
            yield return new NoOpJob(project.Id);
        }
    }

    private sealed class NoOpJob : IIngestionJob
    {
        public NoOpJob(string projectId) => ProjectId = projectId;
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static (IngestionController controller, JsonFileProjectRepository projects, JsonFileProjectMembershipRepository memberships,
        JsonFileDeadLetterRepository deadLetters, JsonFileContentHashRepository contentHashes, IngestionQueue queue, RecordingJobFactory factory, string tempDir)
        MakeSut(string userId = "alice")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        var memberships = new JsonFileProjectMembershipRepository(tempDir);
        var deadLetters = new JsonFileDeadLetterRepository(tempDir);
        var contentHashes = new JsonFileContentHashRepository(tempDir);
        var queue = new IngestionQueue();
        var factory = new RecordingJobFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IIngestionJobFactory>(factory);

        var controller = new IngestionController(queue, projects, memberships, deadLetters, contentHashes, services.BuildServiceProvider());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserPrincipal(userId) } };
        return (controller, projects, memberships, deadLetters, contentHashes, queue, factory, tempDir);
    }

    [Fact]
    public async Task GetDeadLetters_NonMember_ReturnsForbid()
    {
        var (controller, _, _, _, _, _, _, tempDir) = MakeSut();

        var result = await controller.GetDeadLetters("proj1", default);

        Assert.IsType<ForbidResult>(result.Result);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task GetDeadLetters_Member_ReturnsOnlyThatProjectsEntries()
    {
        var (controller, _, memberships, deadLetters, _, _, _, tempDir) = MakeSut();
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
        var (controller, _, memberships, deadLetters, _, _, _, tempDir) = MakeSut(userId: "bob");
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
        var (controller, _, memberships, deadLetters, _, _, _, tempDir) = MakeSut();
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
        var (controller, _, _, _, _, _, _, tempDir) = MakeSut();

        var result = await controller.DismissDeadLetter("does-not-exist", default);

        Assert.IsType<NotFoundResult>(result);
        Directory.Delete(tempDir, recursive: true);
    }

    // U12 (rag-pipeline-reliability-plan): Trigger's own behavior, kept as a regression guard for the
    // shared-helper extraction done to add ForceReindex below.
    [Fact]
    public async Task Trigger_NonMember_ReturnsForbid()
    {
        var (controller, projects, _, _, _, _, factory, tempDir) = MakeSut();
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });

        var result = await controller.Trigger(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Trigger_UnknownProject_ReturnsNotFound()
    {
        var (controller, _, memberships, _, _, _, factory, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");

        var result = await controller.Trigger(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Trigger_AlreadyBusy_ReturnsConflict()
    {
        var (controller, projects, memberships, _, _, queue, factory, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });
        queue.Enqueue(new NoOpJob("proj1"));

        var result = await controller.Trigger(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task Trigger_ValidProject_EnqueuesJobsFromEveryFactory_AndDoesNotClearHashes()
    {
        var (controller, projects, memberships, _, contentHashes, _, factory, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });
        await contentHashes.SetHashAsync("proj1", "src1", "hash1");

        var result = await controller.Trigger(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<AcceptedResult>(result);
        var snapshot = Assert.Single(factory.CallsWithProjectSnapshot);
        Assert.Equal("proj1", snapshot.Id);
        Assert.Equal("hash1", await contentHashes.GetHashAsync("proj1", "src1"));
        Directory.Delete(tempDir, recursive: true);
    }

    // U7: interactive Trigger/ForceReindex have a request-bound caller, so it's threaded through
    // to every registered job factory (attributed to the requesting Admin's TokenUsageEntry rows
    // via KbVectorIndexer -- verified end-to-end in TokenUsageTests / OrgsControllerTests).
    [Fact]
    public async Task Trigger_ValidProject_ThreadsCallerIdToJobFactories()
    {
        var (controller, projects, memberships, _, _, _, factory, tempDir) = MakeSut(userId: "alice");
        await memberships.AddAsync("alice", "proj1");
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });

        await controller.Trigger(new IngestionController.TriggerRequest("proj1"), default);

        Assert.Equal("alice", Assert.Single(factory.TriggeredByUserIds));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ForceReindex_ValidProject_ThreadsCallerIdToJobFactories()
    {
        var (controller, projects, memberships, _, _, _, factory, tempDir) = MakeSut(userId: "alice");
        await memberships.AddAsync("alice", "proj1");
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });

        await controller.ForceReindex(new IngestionController.TriggerRequest("proj1"), default);

        Assert.Equal("alice", Assert.Single(factory.TriggeredByUserIds));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ForceReindex_NonMember_ReturnsForbid()
    {
        var (controller, projects, _, _, _, _, factory, tempDir) = MakeSut();
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });

        var result = await controller.ForceReindex(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ForceReindex_UnknownProject_ReturnsNotFound()
    {
        var (controller, _, memberships, _, _, _, factory, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");

        var result = await controller.ForceReindex(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ForceReindex_AlreadyBusy_ReturnsConflict()
    {
        var (controller, projects, memberships, _, _, queue, factory, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });
        queue.Enqueue(new NoOpJob("proj1"));

        var result = await controller.ForceReindex(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ForceReindex_ValidProject_ClearsContentHashes_AndEnqueuesJobsFromEveryFactory()
    {
        var (controller, projects, memberships, _, contentHashes, _, factory, tempDir) = MakeSut();
        await memberships.AddAsync("alice", "proj1");
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "P1" });
        await contentHashes.SetHashAsync("proj1", "src1", "hash1");
        await contentHashes.SetHashAsync("proj-other", "src2", "hash2");

        var result = await controller.ForceReindex(new IngestionController.TriggerRequest("proj1"), default);

        Assert.IsType<AcceptedResult>(result);
        var snapshot = Assert.Single(factory.CallsWithProjectSnapshot);
        Assert.Equal("proj1", snapshot.Id);
        Assert.Null(await contentHashes.GetHashAsync("proj1", "src1"));
        Assert.Equal("hash2", await contentHashes.GetHashAsync("proj-other", "src2"));
        Directory.Delete(tempDir, recursive: true);
    }
}
