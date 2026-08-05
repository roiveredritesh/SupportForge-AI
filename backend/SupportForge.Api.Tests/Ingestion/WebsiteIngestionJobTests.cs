using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class WebsiteIngestionJobTests
{
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP should not have been called.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _html;
        public StubHandler(string html) => _html = html;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_html) });
    }

    private static WebsiteIngestionJob CreateJob(string url, HttpMessageHandler? handler = null, IVectorStoreService? vectorStore = null)
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var indexer = new KbVectorIndexer(llm.Object, vectorStore ?? new Mock<IVectorStoreService>().Object);
        return new WebsiteIngestionJob("proj1", url, new HttpClient(handler ?? new ThrowingHandler()), indexer, new Mock<IProjectRepository>().Object);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://192.168.1.1/internal")]
    [InlineData("http://172.16.0.1/internal")]
    public async Task RunAsync_RejectsNonPublicOrNonHttpUrls(string url)
    {
        var job = CreateJob(url);

        await Assert.ThrowsAsync<ArgumentException>(() => job.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_RejectsUrl_BeforeMakingHttpRequest()
    {
        // ThrowingHandler blows up if the HTTP client is ever invoked -- proves the SSRF guard
        // runs before any request is made.
        var job = CreateJob("http://localhost/x");

        await Assert.ThrowsAsync<ArgumentException>(() => job.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_FetchesAndExtractsVisibleText_ThenIndexes()
    {
        var html = "<html><head><style>.x{color:red}</style></head><body><script>alert(1)</script><h1>Hello</h1><p>World content.</p></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        IReadOnlyList<VectorDocument>? upserted = null;
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs)
            .Returns(Task.CompletedTask);

        // A literal public IP (not a hostname) so Dns.GetHostAddresses resolves it without any
        // real network DNS lookup -- the SSRF guard still runs, just offline-safely.
        var job = CreateJob("http://93.184.216.34/page", new StubHandler(html), vectorStore.Object);

        await job.RunAsync(CancellationToken.None);

        Assert.NotNull(upserted);
        Assert.Contains(upserted!, d => d.Text.Contains("Hello") && d.Text.Contains("World content."));
        Assert.DoesNotContain(upserted!, d => d.Text.Contains("alert(1)") || d.Text.Contains("color:red"));
    }
}
