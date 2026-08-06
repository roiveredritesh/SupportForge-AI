using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class DocumentIngestionJobTests
{
    private static (Mock<ILlmEmbeddingClient> Llm, Mock<IVectorStoreService> VectorStore, KbVectorIndexer Indexer) MakeIndexer()
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        return (llm, vectorStore, new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object));
    }

    [Fact]
    public async Task RunAsync_Throws_WhenFolderDoesNotExist()
    {
        var (_, _, indexer) = MakeIndexer();
        var job = new DocumentIngestionJob(
            "proj1", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "docs/", indexer, new Mock<IProjectRepository>().Object);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => job.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_IndexesMarkdownAndTextFiles_ThenUpdatesLastSyncedAt()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "doc.md"), "Some KB content.");
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "Plain text notes.");
            await File.WriteAllTextAsync(Path.Combine(folder, "ignored.json"), "{}"); // not .md/.txt -- must be skipped

            var (_, vectorStore, indexer) = MakeIndexer();
            IReadOnlyList<VectorDocument>? upserted = null;
            vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
                .Returns(Task.CompletedTask);

            var projects = new Mock<IProjectRepository>();
            var project = new Project
            {
                Id = "proj1",
                Name = "Test",
                KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs/", null) },
            };
            projects.Setup(p => p.GetByIdAsync("proj1", It.IsAny<CancellationToken>())).ReturnsAsync(project);
            Project? saved = null;
            projects.Setup(p => p.UpsertAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
                .Callback<Project, CancellationToken>((p, _) => saved = p)
                .Returns(Task.CompletedTask);

            var job = new DocumentIngestionJob("proj1", folder, "docs/", indexer, projects.Object);

            await job.RunAsync(CancellationToken.None);

            Assert.NotNull(upserted);
            Assert.Equal(2, upserted!.Count); // one chunk each for doc.md and notes.txt
            Assert.Contains(upserted, d => d.Text.Contains("Some KB content."));
            Assert.Contains(upserted, d => d.Text.Contains("Plain text notes."));
            Assert.DoesNotContain(upserted, d => d.Text.Contains("{}"));
            Assert.NotNull(saved);
            Assert.NotNull(saved!.KbSources[0].LastSyncedAt);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
