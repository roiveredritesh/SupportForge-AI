using Microsoft.Extensions.Logging.Abstractions;
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

    // Routes requests to different fixed HTML by exact URL, for crawl tests where the root page and
    // its linked pages must return distinct content.
    private sealed class MultiUrlStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _htmlByUrl;
        public MultiUrlStubHandler(Dictionary<string, string> htmlByUrl) => _htmlByUrl = htmlByUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_htmlByUrl.TryGetValue(url, out var html))
                throw new InvalidOperationException($"Unexpected request to {url}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }

    private static WebsiteIngestionJob CreateJob(
        string url, HttpMessageHandler? handler = null, IVectorStoreService? vectorStore = null, bool crawlLinkedPages = false)
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var indexer = new KbVectorIndexer(
            llm.Object, vectorStore ?? new Mock<IVectorStoreService>().Object, hashes.Object, new Mock<ITokenUsageRepository>().Object,
            NullLogger<KbVectorIndexer>.Instance);
        return new WebsiteIngestionJob(
            "proj1", url, new HttpClient(handler ?? new ThrowingHandler()), indexer, new Mock<IProjectRepository>().Object, crawlLinkedPages);
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
        var html = "<html><head><title>Welcome Page</title><style>.x{color:red}</style></head><body><script>alert(1)</script><h1>Hello</h1><p>World content.</p></body></html>";
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
        // D1 (gap-closing-solutions.md Phase D, item 1): <title> is now attached as chunk metadata.
        Assert.All(upserted, d => Assert.Equal("Welcome Page", d.Metadata["title"]));
    }

    [Fact]
    public async Task RunAsync_CrawlLinkedPagesFalse_OnlyIndexesTheRootPage_EvenWhenItLinksElsewhere()
    {
        const string root = "http://93.184.216.34/";
        var rootHtml = $"<html><body><p>Root content.</p><a href=\"{root}other\">Other page</a></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        // MultiUrlStubHandler only maps the root URL -- if the job requested "other" too, the test
        // would throw, proving crawlLinkedPages: false never follows the link.
        var handler = new MultiUrlStubHandler(new() { [root] = rootHtml });
        var job = CreateJob(root, handler, vectorStore.Object, crawlLinkedPages: false);

        await job.RunAsync(CancellationToken.None);

        Assert.Single(upserted);
        Assert.Contains("Root content.", upserted[0].Text);
    }

    [Fact]
    public async Task RunAsync_CrawlLinkedPagesTrue_AlsoIndexesSameHostLinkedPages()
    {
        const string root = "http://93.184.216.34/";
        const string linked = "http://93.184.216.34/docs";
        var rootHtml = $"<html><body><p>Root content.</p><a href=\"/docs\">Docs</a></body></html>";
        var linkedHtml = "<html><body><p>Docs page content.</p></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        var handler = new MultiUrlStubHandler(new() { [root] = rootHtml, [linked] = linkedHtml });
        var job = CreateJob(root, handler, vectorStore.Object, crawlLinkedPages: true);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(2, upserted.Count);
        Assert.Contains(upserted, d => d.Text.Contains("Root content."));
        Assert.Contains(upserted, d => d.Text.Contains("Docs page content."));
    }

    [Fact]
    public async Task RunAsync_CrawlLinkedPagesTrue_SkipsOffHostLinks()
    {
        const string root = "http://93.184.216.34/";
        var rootHtml = "<html><body><p>Root content.</p><a href=\"https://a-different-host.example/page\">Elsewhere</a></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        // MultiUrlStubHandler only maps the root URL -- a request to the off-host link would throw.
        var handler = new MultiUrlStubHandler(new() { [root] = rootHtml });
        var job = CreateJob(root, handler, vectorStore.Object, crawlLinkedPages: true);

        await job.RunAsync(CancellationToken.None);

        Assert.Single(upserted);
    }

    [Fact]
    public async Task RunAsync_CrawlLinkedPagesTrue_OneLinkedPageFails_StillIndexesTheRest()
    {
        const string root = "http://93.184.216.34/";
        const string broken = "http://93.184.216.34/broken";
        var rootHtml = $"<html><body><p>Root content.</p><a href=\"/broken\">Broken</a></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        var handler = new FailingUrlHandler(root, rootHtml, broken);
        var job = CreateJob(root, handler, vectorStore.Object, crawlLinkedPages: true);

        await job.RunAsync(CancellationToken.None);

        Assert.Single(upserted);
        Assert.Contains("Root content.", upserted[0].Text);
    }

    private sealed class FailingUrlHandler : HttpMessageHandler
    {
        private readonly string _okUrl;
        private readonly string _okHtml;
        private readonly string _failingUrl;
        public FailingUrlHandler(string okUrl, string okHtml, string failingUrl)
        {
            _okUrl = okUrl;
            _okHtml = okHtml;
            _failingUrl = failingUrl;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == _failingUrl) throw new HttpRequestException("simulated fetch failure");
            if (url == _okUrl) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_okHtml) });
            throw new InvalidOperationException($"Unexpected request to {url}");
        }
    }
}
