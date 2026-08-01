using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.Ingestion.Graphify;
using SupportForge.VectorStore;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class IngestionJobFactoryTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddTransient<ILlmClient>(_ => new Mock<ILlmClient>().Object);
        services.AddTransient<IVectorStoreService>(_ => new Mock<IVectorStoreService>().Object);
        services.AddSingleton<IProjectRepository>(new Mock<IProjectRepository>().Object);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<GitRepoSyncService>();
        services.AddSingleton(new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance));
        return services.BuildServiceProvider();
    }

    private static ILlmClient GetLlmField(object job, string fieldName) =>
        (ILlmClient)job.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(job)!;

    [Fact]
    public void DocumentIngestionJobFactory_ResolvesFreshLlmClient_OnEachCreateJobsCall()
    {
        using var provider = BuildProvider();
        var factory = new DocumentIngestionJobFactory(provider, Path.GetTempPath());
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs/", null) },
        };

        var firstJob = factory.CreateJobs(project).Single();
        var secondJob = factory.CreateJobs(project).Single();

        var firstLlm = GetLlmField(firstJob, "_llm");
        var secondLlm = GetLlmField(secondJob, "_llm");

        Assert.NotSame(firstLlm, secondLlm);
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

        var jobs = factory.CreateJobs(project).ToList();

        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal("proj1", j.ProjectId));
    }
}
