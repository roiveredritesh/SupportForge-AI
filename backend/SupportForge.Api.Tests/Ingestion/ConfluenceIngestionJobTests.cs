using System.Net;
using Microsoft.Extensions.Options;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class ConfluenceIngestionJobTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json) });
    }

    [Fact]
    public async Task RunAsync_FetchesMarkdown_IndexesIt_ThenUpdatesLastSyncedAt()
    {
        var json = """
            { "title": "Runbook", "body": { "storage": { "value": "<p>Restart the service.</p>" } } }
            """;
        var fetcher = new ConfluencePageFetcher(
            new HttpClient(new StubHandler(json)), Options.Create(new ConfluenceOptions { BaseUrl = "http://confluence.example.com" }));

        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var indexer = new KbVectorIndexer(llm.Object, vectorStore.Object, hashes.Object, new Mock<ITokenUsageRepository>().Object);

        var projects = new Mock<IProjectRepository>();
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Confluence, "12345", null) },
        };
        projects.Setup(p => p.GetByIdAsync("proj1", It.IsAny<CancellationToken>())).ReturnsAsync(project);
        Project? saved = null;
        projects.Setup(p => p.UpsertAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
            .Callback<Project, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        var job = new ConfluenceIngestionJob("proj1", "12345", fetcher, indexer, projects.Object);

        await job.RunAsync(CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.Contains(upserted!, d => d.Text.Contains("Restart the service."));
        // D1 (gap-closing-solutions.md Phase D, item 1): the page title ConfluencePageFetcher already
        // parses out is now attached as chunk metadata instead of being discarded.
        Assert.All(upserted, d => Assert.Equal("Runbook", d.Metadata["title"]));
        Assert.NotNull(saved);
        Assert.NotNull(saved!.KbSources[0].LastSyncedAt);
    }
}
