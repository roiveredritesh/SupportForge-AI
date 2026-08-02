using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GitHubFolderIngestionJobTests
{
    private static string SeedBareRepo(params string[] filesToCommit)
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), "remote-" + Guid.NewGuid());
        Repository.Init(remoteDir, isBare: true);

        var seedDir = Path.Combine(Path.GetTempPath(), "seed-" + Guid.NewGuid());
        Repository.Clone(remoteDir, seedDir);
        foreach (var relativePath in filesToCommit)
        {
            var fullPath = Path.Combine(seedDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "content");
        }
        using (var seedRepo = new Repository(seedDir))
        {
            Commands.Stage(seedRepo, "*");
            var sig = new Signature("test", "test@test.com", DateTimeOffset.Now);
            seedRepo.Commit("initial commit", sig, sig);
            seedRepo.Network.Push(seedRepo.Branches["master"]);
        }
        TryDelete(seedDir);
        return remoteDir;
    }

    [Fact]
    public async Task RunAsync_ClonesRepo_ThenThrows_WhenSubPathDoesNotExistAfterClone()
    {
        var remoteDir = SeedBareRepo("readme.md");
        var localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid());
        try
        {
            var job = new GitHubFolderIngestionJob(
                "proj1", remoteDir, "master", localDir, "docs/that-does-not-exist", "https://github.com/acme/widgets/tree/master/docs/that-does-not-exist",
                new GitRepoSyncService(new ConfigurationBuilder().Build()),
                new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance),
                new Dictionary<string, string?>(),
                new Mock<IProjectRepository>().Object);

            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => job.RunAsync(CancellationToken.None));

            // The clone itself must have succeeded before the subpath check ran.
            Assert.True(File.Exists(Path.Combine(localDir, "readme.md")));
        }
        finally
        {
            TryDelete(remoteDir);
            TryDelete(localDir);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_ClonesAndExtracts_ThenUpdatesLastSyncedAt()
    {
        Skip.If(
            new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "MOONSHOT_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "DEEPSEEK_API_KEY" }
                .All(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))),
            "requires a graphify-supported LLM API key for semantic extraction of doc content");

        var remoteDir = SeedBareRepo("docs/doc.md");
        var localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid());
        try
        {
            var projects = new Mock<IProjectRepository>();
            var location = "https://github.com/acme/widgets/tree/master/docs";
            var project = new Project
            {
                Id = "proj1",
                Name = "Test",
                KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, location, null) },
            };
            projects.Setup(p => p.GetByIdAsync("proj1", It.IsAny<CancellationToken>())).ReturnsAsync(project);
            Project? upserted = null;
            projects.Setup(p => p.UpsertAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
                .Callback<Project, CancellationToken>((p, _) => upserted = p)
                .Returns(Task.CompletedTask);

            var job = new GitHubFolderIngestionJob(
                "proj1", remoteDir, "master", localDir, "docs", location,
                new GitRepoSyncService(new ConfigurationBuilder().Build()),
                new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance),
                new Dictionary<string, string?>(),
                projects.Object);

            await job.RunAsync(CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(localDir, "docs", "graphify-out")));
            Assert.NotNull(upserted);
            Assert.NotNull(upserted!.KbSources[0].LastSyncedAt);
        }
        finally
        {
            TryDelete(remoteDir);
            TryDelete(localDir);
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
