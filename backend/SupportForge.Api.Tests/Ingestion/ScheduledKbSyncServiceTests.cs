using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SupportForge.Agents;
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

        var docServices = new ServiceCollection();
        docServices.AddHttpClient();
        docServices.AddSingleton<IProjectRepository>(projects);
        docServices.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        docServices.AddSingleton<GitRepoSyncService>();
        docServices.AddSingleton(new ConfluencePageFetcher(new HttpClient(), Options.Create(new ConfluenceOptions())));
        docServices.AddSingleton(new Mock<ILlmEmbeddingClient>().Object);
        docServices.AddSingleton(new Mock<IVectorStoreService>().Object);
        docServices.AddSingleton<KbVectorIndexer>();
        using var docProvider = docServices.BuildServiceProvider();
        var docFactory = new DocumentIngestionJobFactory(docProvider, tempDir);

        await MakeSut(projects, queue, docFactory).RunOnceAsync(default);

        Assert.True(queue.IsBusy("proj1"));
        Directory.Delete(tempDir, recursive: true);
    }

    private sealed class NoOpJob : IIngestionJob
    {
        public NoOpJob(string projectId) => ProjectId = projectId;
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
