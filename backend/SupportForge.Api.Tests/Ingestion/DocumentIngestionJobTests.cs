using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class DocumentIngestionJobTests
{
    [Fact]
    public async Task RunAsync_Throws_WhenFolderDoesNotExist()
    {
        var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
        var job = new DocumentIngestionJob("proj1", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "docs/", graphify, new Mock<IProjectRepository>().Object);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => job.RunAsync(CancellationToken.None));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_ExtractsFolderAndUpdatesLastSyncedAt()
    {
        // Unlike code (AST-only, no LLM), a doc/MD corpus needs graphify's semantic extraction,
        // which needs a real provider key -- this is the token cost the plan calls out, not a gap
        // in this test.
        Skip.If(
            new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "MOONSHOT_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "DEEPSEEK_API_KEY" }
                .All(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))),
            "requires a graphify-supported LLM API key for semantic extraction of doc content");

        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "doc.md"), "# Hello\n\nSome KB content.");

            var graphify = new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance);
            var projects = new Mock<IProjectRepository>();
            var project = new Project
            {
                Id = "proj1",
                Name = "Test",
                KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs/", null) },
            };
            projects.Setup(p => p.GetByIdAsync("proj1", It.IsAny<CancellationToken>())).ReturnsAsync(project);
            Project? upserted = null;
            projects.Setup(p => p.UpsertAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
                .Callback<Project, CancellationToken>((p, _) => upserted = p)
                .Returns(Task.CompletedTask);

            var job = new DocumentIngestionJob("proj1", folder, "docs/", graphify, projects.Object);

            await job.RunAsync(CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(folder, "graphify-out")));
            Assert.NotNull(upserted);
            Assert.NotNull(upserted!.KbSources[0].LastSyncedAt);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
