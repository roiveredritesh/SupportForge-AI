using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Ingestion", "Fixtures", name);

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
            var ext = Path.GetExtension(relativePath);
            if (ext is ".pdf" or ".docx" or ".pptx")
                File.Copy(FixturePath("sample" + ext), fullPath);
            else
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
        // GitHubFolderIngestionJob always passes a PruneScope now; an empty known-refs list means
        // nothing looks stale, matching every existing test's fresh-hash-repo assumption.
        hashes.Setup(h => h.GetSourceRefsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        return new KbVectorIndexer(
            llm.Object, vectorStore ?? new Mock<IVectorStoreService>().Object, hashes.Object, new Mock<ITokenUsageRepository>().Object,
            NullLogger<KbVectorIndexer>.Instance);
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
                new Mock<IProjectRepository>().Object,
                NullLogger<GitHubFolderIngestionJob>.Instance);

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
                projects.Object,
                NullLogger<GitHubFolderIngestionJob>.Instance);

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

    // U10: parity check -- GitHubFolderIngestionJob applies the same extension set and extraction
    // path as DocumentIngestionJob (.md/.txt/.pdf/.docx/.pptx supported, everything else skipped).
    [Fact]
    public async Task RunAsync_IndexesPdfDocxPptx_AndSkipsUnsupportedExtension()
    {
        var remoteDir = SeedBareRepo("docs/doc.md", "docs/sample.pdf", "docs/sample.docx", "docs/sample.pptx", "docs/ignored.xlsx");
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
            projects.Setup(p => p.UpsertAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var job = new GitHubFolderIngestionJob(
                "proj1", remoteDir, "master", localDir, "docs", location,
                new GitRepoSyncService(new ConfigurationBuilder().Build()),
                MakeIndexer(vectorStore.Object),
                projects.Object,
                NullLogger<GitHubFolderIngestionJob>.Instance);

            await job.RunAsync(CancellationToken.None);

            Assert.NotNull(upserted);
            Assert.Equal(4, upserted!.Count); // md, pdf, docx, pptx -- xlsx excluded
            Assert.Contains(upserted, d => d.Text.Contains("content")); // doc.md
            Assert.Contains(upserted, d => d.Text.Contains("Hello PDF extraction test"));
            Assert.Contains(upserted, d => d.Text.Contains("This is a sample paragraph"));
            Assert.Contains(upserted, d => d.Text.Contains("Slide one content."));
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
