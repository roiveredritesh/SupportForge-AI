using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SupportForge.Agents;
using SupportForge.Api.Tests.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

// C4 (gap-closing-solutions.md Phase C, item 4): the scheduled-re-sync half of freshness automation.
// RunOnceAsync is internal (InternalsVisibleTo) so a tick can be driven directly instead of racing
// PeriodicTimer / BackgroundService's fire-and-forget start.
public class ScheduledKbSyncServiceTests
{
    // A DocumentIngestionJobFactory backed by an empty, unconfigured IServiceProvider: CreateJobs
    // would throw InvalidOperationException (unregistered dependencies) the moment it's actually
    // invoked, so tests asserting "skipped, never reached the factory" get a real proof of that
    // rather than a hand-rolled mock.
    private static DocumentIngestionJobFactory UnusableFactory() =>
        new(new ServiceCollection().BuildServiceProvider(), Path.GetTempPath());

    private static ScheduledKbSyncService MakeSut(IProjectRepository projects, IngestionQueue queue, DocumentIngestionJobFactory docFactory) =>
        new(projects, queue, docFactory, new ConfigurationBuilder().Build(), NullLogger<ScheduledKbSyncService>.Instance);

    [Fact]
    public async Task RunOnceAsync_SkipsProjectsWithNoKbSources_WithoutTouchingTheFactory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project { Id = "proj1", Name = "No KB sources" });
        var queue = new IngestionQueue();

        await MakeSut(projects, queue, UnusableFactory()).RunOnceAsync(default);

        Assert.False(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsProjectsAlreadyIngesting_WithoutTouchingTheFactory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "Busy Project",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Website, "https://example.com", null) },
        });
        var queue = new IngestionQueue();
        queue.Enqueue(new NoOpJob("proj1")); // simulate an in-flight ingestion

        // UnusableFactory would throw InvalidOperationException if CreateJobs were ever called --
        // completing without an exception proves the busy check short-circuits before that.
        await MakeSut(projects, queue, UnusableFactory()).RunOnceAsync(default);

        Assert.True(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunOnceAsync_EnqueuesJobs_ForNonBusyProjectsWithKbSources()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "Website KB",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Website, "https://example.com", null) },
        });
        var queue = new IngestionQueue();
        var docFactory = MakeRealDocFactory(projects, tempDir);

        await MakeSut(projects, queue, docFactory).RunOnceAsync(default);

        Assert.True(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    private static DocumentIngestionJobFactory MakeRealDocFactory(IProjectRepository projects, string tempDir)
    {
        var docServices = new ServiceCollection();
        docServices.AddHttpClient();
        docServices.AddSingleton(projects);
        docServices.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        docServices.AddSingleton<GitRepoSyncService>();
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        docServices.AddSingleton(hashes.Object);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        docServices.AddSingleton(new IngestionImageCaptioner(new Mock<ILlmChatClient>().Object));
        docServices.AddSingleton(new ConfluencePageFetcher(
            new HttpClient(), Options.Create(new ConfluenceOptions()),
            new IngestionImageCaptioner(new Mock<ILlmChatClient>().Object), httpClientFactory.Object));
        docServices.AddSingleton(new Mock<ILlmEmbeddingClient>().Object);
        docServices.AddSingleton(new Mock<IVectorStoreService>().Object);
        docServices.AddSingleton(new Mock<ITokenUsageRepository>().Object);
        docServices.AddSingleton<KbVectorIndexer>();
        return new DocumentIngestionJobFactory(docServices.BuildServiceProvider(), tempDir);
    }

    [Fact]
    public async Task RunOnceAsync_ProjectWithCustomInterval_NotYetDue_IsSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "Custom Interval, Recently Synced",
            ScheduledSyncIntervalHours = 48, // due only after 48h
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Website, "https://example.com", DateTimeOffset.UtcNow.AddHours(-1)), // synced 1h ago
            },
        });
        var queue = new IngestionQueue();

        // UnusableFactory would throw if CreateJobs were called -- proves the not-yet-due project
        // never reaches the factory.
        await MakeSut(projects, queue, UnusableFactory()).RunOnceAsync(default);

        Assert.False(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunOnceAsync_ProjectWithCustomInterval_PastDue_IsEnqueued()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "Custom Interval, Due",
            ScheduledSyncIntervalHours = 2, // due after just 2h
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Website, "https://example.com", DateTimeOffset.UtcNow.AddHours(-3)), // synced 3h ago
            },
        });
        var queue = new IngestionQueue();
        var docFactory = MakeRealDocFactory(projects, tempDir);

        await MakeSut(projects, queue, docFactory).RunOnceAsync(default);

        Assert.True(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task RunOnceAsync_ProjectWithoutOverride_UsesGlobalDefaultInterval()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "proj1",
            Name = "No Override, Recently Synced",
            // No ScheduledSyncIntervalHours set -- falls back to the global default (24h).
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Website, "https://example.com", DateTimeOffset.UtcNow.AddHours(-1)),
            },
        });
        var queue = new IngestionQueue();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Freshness:ScheduledSyncIntervalHours"] = "24" })
            .Build();
        var sut = new ScheduledKbSyncService(projects, queue, UnusableFactory(), config, NullLogger<ScheduledKbSyncService>.Instance);

        // 1h-old sync against a 24h global default -- not due, UnusableFactory would throw if reached.
        await sut.RunOnceAsync(default);

        Assert.False(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    // A project whose jobs can't be built (here: a Documents source naming a repo the project
    // doesn't have) must be skipped on its own, not abort the rest of the pass.
    [Fact]
    public async Task RunOnceAsync_ProjectWhoseJobsCannotBeBuilt_IsSkipped_AndOtherProjectsStillEnqueued()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projects = new JsonFileProjectRepository(tempDir);
        await projects.UpsertAsync(new Project
        {
            Id = "broken",
            Name = "Documents source for a repo the project doesn't have",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs", null, RepoOwner: "acme", RepoName: "missing") },
        });
        await projects.UpsertAsync(new Project
        {
            Id = "healthy",
            Name = "Website KB",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Website, "https://example.com", null) },
        });
        var queue = new IngestionQueue();
        var logger = new ListLogger<ScheduledKbSyncService>();
        var sut = new ScheduledKbSyncService(projects, queue, MakeRealDocFactory(projects, tempDir), new ConfigurationBuilder().Build(), logger);

        await sut.RunOnceAsync(default);

        Assert.False(queue.IsBusy("broken"));
        Assert.True(queue.IsBusy("healthy"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("broken"));
        Directory.Delete(tempDir, recursive: true);
    }

    // An exception escaping ExecuteAsync stops the whole host by default, which is how a single
    // failing pass used to take down the API (and, in integration tests, whichever test shared the
    // host). The service must log it and keep running until the next poll.
    [Fact]
    public async Task ExecuteAsync_SyncPassThatThrows_IsLogged_AndTheServiceKeepsRunning()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("projects.json unreadable"));
        var logger = new ListLogger<ScheduledKbSyncService>();
        var sut = new ScheduledKbSyncService(projects.Object, new IngestionQueue(), UnusableFactory(), new ConfigurationBuilder().Build(), logger);

        await sut.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logger.Entries.ToArray().Any(e => e.Level == LogLevel.Error) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Contains(logger.Entries.ToArray(), e => e.Level == LogLevel.Error && e.Exception is IOException);
        Assert.NotNull(sut.ExecuteTask);
        Assert.False(sut.ExecuteTask!.IsCompleted);

        await sut.StopAsync(CancellationToken.None);
    }

    private sealed class NoOpJob : IIngestionJob
    {
        public NoOpJob(string projectId) => ProjectId = projectId;
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
