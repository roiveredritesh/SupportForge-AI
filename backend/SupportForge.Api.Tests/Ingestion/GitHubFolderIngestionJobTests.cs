using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
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

    private static KbVectorIndexer MakeIndexer(IVectorStoreService? vectorStore = null)
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        return new KbVectorIndexer(llm.Object, vectorStore ?? new Mock<IVectorStoreService>().Object, hashes.Object);
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
                MakeIndexer(),
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

    [Fact]
    public async Task RunAsync_ClonesAndIndexes_ThenUpdatesLastSyncedAt()
    {
        var remoteDir = SeedBareRepo("docs/doc.md");
        var localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid());
        try
        {
            var vectorStore = new Mock<IVectorStoreService>();
            IReadOnlyList<VectorDocument>? upserted = null;
            vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
                .Returns(Task.CompletedTask);

            var projects = new Mock<IProjectRepository>();
            var location = "https://github.com/acme/widgets/tree/master/docs";
            var project = new Project
            {
                Id = "proj1",
                Name = "Test",
                KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, location, null) },
            };
            projects.Setup(p => p.GetByIdAsync("proj1", It.IsAny<CancellationToken>())).ReturnsAsync(project);
            Project? saved = null;
            projects.Setup(p => p.UpsertAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
                .Callback<Project, CancellationToken>((p, _) => saved = p)
                .Returns(Task.CompletedTask);

            var job = new GitHubFolderIngestionJob(
                "proj1", remoteDir, "master", localDir, "docs", location,
                new GitRepoSyncService(new ConfigurationBuilder().Build()),
                MakeIndexer(vectorStore.Object),
                projects.Object);

            await job.RunAsync(CancellationToken.None);

            Assert.NotNull(upserted);
            Assert.Contains(upserted!, d => d.Text.Contains("content"));
            Assert.NotNull(saved);
            Assert.NotNull(saved!.KbSources[0].LastSyncedAt);
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
