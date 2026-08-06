using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

// C4 (gap-closing-solutions.md Phase C, item 4): the GitHub webhook is the "push re-triggers
// ingestion" half of freshness automation. Tested by direct construction (not WebApplicationFactory)
// so job enqueueing is observed deterministically via a fake IIngestionJobFactory, instead of racing
// the real IngestionBackgroundService that would be running in a full test host.
public class WebhooksControllerTests
{
    private const string Secret = "test-webhook-secret";

    private sealed class RecordingJobFactory : IIngestionJobFactory
    {
        public List<Project> CallsWithProjectSnapshot { get; } = new();
        public IEnumerable<IIngestionJob> CreateJobs(Project project)
        {
            CallsWithProjectSnapshot.Add(project);
            yield return new NoOpJob(project.Id);
        }
    }

    private sealed class NoOpJob : IIngestionJob
    {
        public NoOpJob(string projectId) => ProjectId = projectId;
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static (WebhooksController controller, IngestionQueue queue, RecordingJobFactory factory, string tempDir) MakeSut(
        string? secret = Secret)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        var queue = new IngestionQueue();
        var factory = new RecordingJobFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IIngestionJobFactory>(factory);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(secret is null ? [] : new Dictionary<string, string?> { ["GitHubWebhook:Secret"] = secret })
            .Build();

        var controller = new WebhooksController(projects, queue, services.BuildServiceProvider(), config, NullLogger<WebhooksController>.Instance);
        return (controller, queue, factory, tempDir);
    }

    private static void SetRequest(WebhooksController controller, string body, string? eventType = "push", string? signature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var httpContext = new DefaultHttpContext { Request = { Body = new MemoryStream(bytes), ContentLength = bytes.Length } };
        if (eventType is not null) httpContext.Request.Headers["X-GitHub-Event"] = eventType;
        if (signature is not null) httpContext.Request.Headers["X-Hub-Signature-256"] = signature;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static string SignPayload(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private const string PushPayload = """{ "repository": { "name": "widget-api", "owner": { "name": "acme" } } }""";

    [Fact]
    public async Task MissingSignatureHeader_ReturnsUnauthorized()
    {
        var (controller, _, _, tempDir) = MakeSut();
        SetRequest(controller, PushPayload, signature: null);

        var result = await controller.GitHub(default);

        Assert.IsType<UnauthorizedResult>(result);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task InvalidSignature_ReturnsUnauthorized()
    {
        var (controller, _, _, tempDir) = MakeSut();
        SetRequest(controller, PushPayload, signature: "sha256=" + new string('0', 64));

        var result = await controller.GitHub(default);

        Assert.IsType<UnauthorizedResult>(result);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task NoSecretConfigured_ReturnsServiceUnavailable()
    {
        var (controller, _, _, tempDir) = MakeSut(secret: null);
        SetRequest(controller, PushPayload, signature: SignPayload(PushPayload, Secret));

        var result = await controller.GitHub(default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task NonPushEvent_ReturnsOk_WithoutEnqueueing()
    {
        var (controller, queue, factory, tempDir) = MakeSut();
        SetRequest(controller, PushPayload, eventType: "ping", signature: SignPayload(PushPayload, Secret));

        var result = await controller.GitHub(default);

        Assert.IsType<OkResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ValidPush_NoMatchingProject_ReturnsAccepted_EnqueuesNothing()
    {
        var (controller, _, factory, tempDir) = MakeSut();
        SetRequest(controller, PushPayload, signature: SignPayload(PushPayload, Secret));

        var result = await controller.GitHub(default);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot);
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ValidPush_MatchingProject_EnqueuesOnlyThePushedRepo_NotOtherReposOrKbSources()
    {
        var (controller, queue, factory, tempDir) = MakeSut();
        var projects = new JsonFileProjectRepository(tempDir);
        var pushedRepo = new GitHubRepoConfig("acme", "widget-api", "main", null);
        var otherRepo = new GitHubRepoConfig("acme", "other-repo", "main", null);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "Widget Project",
            Repos = new List<GitHubRepoConfig> { pushedRepo, otherRepo },
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Confluence, "SPACE1", null) },
        });
        SetRequest(controller, PushPayload, signature: SignPayload(PushPayload, Secret));

        var result = await controller.GitHub(default);

        Assert.IsType<AcceptedResult>(result);
        var snapshot = Assert.Single(factory.CallsWithProjectSnapshot);
        Assert.Equal("proj1", snapshot.Id);
        var repo = Assert.Single(snapshot.Repos);
        Assert.Equal("widget-api", repo.Repo);
        Assert.Empty(snapshot.KbSources); // pseudo-project carries no KB sources -- a push shouldn't re-sync Confluence
        Assert.True(queue.IsBusy("proj1"));

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ValidPush_ProjectAlreadyIngesting_IsSkipped()
    {
        var (controller, queue, factory, tempDir) = MakeSut();
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "Widget Project",
            Repos = new List<GitHubRepoConfig> { new("acme", "widget-api", "main", null) },
        });
        queue.Enqueue(new NoOpJob("proj1")); // simulate an in-flight ingestion for this project
        SetRequest(controller, PushPayload, signature: SignPayload(PushPayload, Secret));

        var result = await controller.GitHub(default);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(factory.CallsWithProjectSnapshot); // skipped, not re-enqueued on top of the busy job
        Directory.Delete(tempDir, recursive: true);
    }
}
