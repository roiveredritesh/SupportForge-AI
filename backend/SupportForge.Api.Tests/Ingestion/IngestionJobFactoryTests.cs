using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class IngestionJobFactoryTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<IProjectRepository>(new Mock<IProjectRepository>().Object);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<GitRepoSyncService>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        services.AddSingleton(new IngestionImageCaptioner(new Mock<ILlmChatClient>().Object));
        services.AddSingleton(new ConfluencePageFetcher(
            new HttpClient(), Options.Create(new ConfluenceOptions()),
            new IngestionImageCaptioner(new Mock<ILlmChatClient>().Object), httpClientFactory.Object));
        services.AddSingleton(new Mock<ILlmEmbeddingClient>().Object);
        services.AddSingleton(new Mock<IVectorStoreService>().Object);
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        services.AddSingleton(hashes.Object);
        services.AddSingleton(new Mock<ITokenUsageRepository>().Object);
        services.AddSingleton<KbVectorIndexer>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DocumentIngestionJobFactory_ResolvesRepoAssociatedSource_AgainstItsOwnRepo()
    {
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig>
            {
                new("owner", "repo-a", "main", null),
                new("owner", "repo-b", "main", null),
            },
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Documents, "docs/", null, RepoOwner: "owner", RepoName: "repo-b"),
            },
        };

        var job = Assert.IsType<DocumentIngestionJob>(factory.CreateJobs(project, null).Single());

        var folderPath = (string)job.GetType()
            .GetField("_folderPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(job)!;
        Assert.Equal(Path.Combine(Path.GetTempPath(), "proj1", "repo-b", "docs/"), folderPath);
    }

    [Fact]
    public void DocumentIngestionJobFactory_Throws_WhenSourceReferencesUnconfiguredRepo()
    {
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig> { new("owner", "repo-a", "main", null) },
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Documents, "docs/", null, RepoOwner: "owner", RepoName: "repo-b"),
            },
        };

        Assert.Throws<InvalidOperationException>(() => factory.CreateJobs(project, null).ToList());
    }

    [Fact]
    public void DocumentIngestionJobFactory_TreatsSourceWithNoRepoAssociation_AsStandaloneAbsolutePath()
    {
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig> { new("owner", "repo-a", "main", null) },
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "/standalone/docs", null) },
        };

        var job = Assert.IsType<DocumentIngestionJob>(factory.CreateJobs(project, null).Single());

        var folderPath = (string)job.GetType()
            .GetField("_folderPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(job)!;
        Assert.Equal("/standalone/docs", folderPath);
    }

    [Fact]
    public void DocumentIngestionJobFactory_GitHubUrlLocation_CreatesGitHubFolderIngestionJob()
    {
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Documents, "https://github.com/acme/widgets/tree/main/docs", null),
            },
        };

        var job = Assert.IsType<GitHubFolderIngestionJob>(factory.CreateJobs(project, null).Single());
        Assert.Equal("proj1", job.ProjectId);
    }

    [Fact]
    public void DocumentIngestionJobFactory_GitHubUrlLocation_IgnoredWhenRepoAssociationIsSet()
    {
        // A URL-shaped Location tied to a configured repo (RepoOwner/RepoName set) still means
        // "subpath inside that repo's own clone" -- only a standalone Location gets URL-parsed.
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig> { new("owner", "repo-a", "main", null) },
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Documents, "https://github.com/acme/widgets/tree/main/docs", null, RepoOwner: "owner", RepoName: "repo-a"),
            },
        };

        Assert.IsType<DocumentIngestionJob>(factory.CreateJobs(project, null).Single());
    }

    [Fact]
    public void DocumentIngestionJobFactory_CreatesWebsiteAndConfluenceJobs_ForThoseSourceTypes()
    {
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Website, "https://example.com/docs", null),
                new(KbSourceType.Confluence, "12345", null),
            },
        };

        var jobs = factory.CreateJobs(project, null).ToList();

        Assert.Single(jobs.OfType<WebsiteIngestionJob>());
        Assert.Single(jobs.OfType<ConfluenceIngestionJob>());
    }

    [Fact]
    public void CodeIngestionJobFactory_CreatesOneJobPerConfiguredRepo()
    {
        using var provider = BuildProvider();
        var factory = new CodeIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            Repos = new List<GitHubRepoConfig>
            {
                new("owner", "repo-a", "main", null),
                new("owner", "repo-b", "main", null),
            },
        };

        var jobs = factory.CreateJobs(project, null).ToList();

        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal("proj1", j.ProjectId));
    }
}
