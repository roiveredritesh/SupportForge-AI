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
        string url, HttpMessageHandler? handler = null, IVectorStoreService? vectorStore = null, bool crawlLinkedPages = false,
        IngestionImageCaptioner? captioner = null)
    {
        var llm = new Mock<ILlmEmbeddingClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingPurpose>())).ReturnsAsync(new float[] { 0.1f });
        var hashes = new Mock<IContentHashRepository>();
        hashes.Setup(h => h.GetHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var indexer = new KbVectorIndexer(
            llm.Object, vectorStore ?? new Mock<IVectorStoreService>().Object, hashes.Object, new Mock<ITokenUsageRepository>().Object,
            NullLogger<KbVectorIndexer>.Instance);
        // Default captioner has SupportsVision: false, so CaptionAsync short-circuits to a placeholder
        // without ever calling back into the HTTP handler to fetch image bytes -- tests that care about
        // the image-fetch/caption path pass their own captioner explicitly.
        return new WebsiteIngestionJob(
            "proj1", url, new HttpClient(handler ?? new ThrowingHandler()), indexer, new Mock<IProjectRepository>().Object,
            captioner ?? new IngestionImageCaptioner(new Mock<ILlmChatClient>().Object), crawlLinkedPages);
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

    // Routes by exact URL to either an HTML string response or a raw byte response, for tests that
    // fetch both a page (HTML) and an image (bytes) through the same HttpClient.
    private sealed class ImageAwareStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _htmlByUrl;
        private readonly Dictionary<string, byte[]> _bytesByUrl;
        private readonly HashSet<string> _failingUrls;
        public ImageAwareStubHandler(Dictionary<string, string> htmlByUrl, Dictionary<string, byte[]>? bytesByUrl = null, HashSet<string>? failingUrls = null)
        {
            _htmlByUrl = htmlByUrl;
            _bytesByUrl = bytesByUrl ?? new();
            _failingUrls = failingUrls ?? new();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (_failingUrls.Contains(url)) throw new HttpRequestException("simulated image fetch failure");
            if (_htmlByUrl.TryGetValue(url, out var html))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) });
            if (_bytesByUrl.TryGetValue(url, out var bytes))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            throw new InvalidOperationException($"Unexpected request to {url}");
        }
    }

    private static readonly byte[] PngMagicBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static Mock<ILlmChatClient> VisionLlm(string caption)
    {
        var llm = new Mock<ILlmChatClient>();
        llm.Setup(l => l.SupportsVision).Returns(true);
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(caption);
        return llm;
    }

    [Fact]
    public async Task RunAsync_HtmlTable_ProducesMarkdownTable_NotFlattenedText()
    {
        const string root = "http://93.184.216.34/page";
        var html = "<html><body><table><tr><th>Name</th><th>Value</th></tr><tr><td>A</td><td>1</td></tr></table></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        var job = CreateJob(root, new StubHandler(html), vectorStore.Object);
        await job.RunAsync(CancellationToken.None);

        Assert.Contains(upserted, d => d.Text.Contains("| Name | Value |") && d.Text.Contains("| A | 1 |"));
    }

    [Fact]
    public async Task RunAsync_ImgWithAlt_ProducesCaptionText_NotDroppedAltText()
    {
        const string root = "http://93.184.216.34/page";
        const string image = "http://93.184.216.34/img.png";
        var html = $"<html><body><p>Intro.</p><img src=\"{image}\" alt=\"a diagram\" /></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        var handler = new ImageAwareStubHandler(new() { [root] = html }, new() { [image] = PngMagicBytes });
        var captioner = new IngestionImageCaptioner(VisionLlm("A bar chart showing values.").Object);
        var job = CreateJob(root, handler, vectorStore.Object, captioner: captioner);

        await job.RunAsync(CancellationToken.None);

        Assert.Contains(upserted, d => d.Text.Contains("[image: A bar chart showing values.]"));
        Assert.DoesNotContain(upserted, d => d.Text.Contains("{{IMAGE:"));
    }

    [Fact]
    public async Task RunAsync_ImageUrlResolvesToPrivateAddress_RejectedBySsrfGuard_FallsBackToPlaceholder()
    {
        const string root = "http://93.184.216.34/page";
        const string image = "http://127.0.0.1/img.png";
        var html = $"<html><body><img src=\"{image}\" alt=\"internal diagram\" /></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        // Only the root page is stubbed -- if the SSRF guard didn't reject the image URl before
        // fetching, ImageAwareStubHandler would throw "Unexpected request" for the private address.
        var handler = new ImageAwareStubHandler(new() { [root] = html });
        var visionLlm = VisionLlm("should never be used");
        var captioner = new IngestionImageCaptioner(visionLlm.Object);
        var job = CreateJob(root, handler, vectorStore.Object, captioner: captioner);

        await job.RunAsync(CancellationToken.None);

        Assert.Single(upserted);
        Assert.Contains("[image: internal diagram]", upserted[0].Text);
        visionLlm.Verify(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ImageFetchFails_FallsBackToPlaceholder_WithoutFailingTheWholePage()
    {
        const string root = "http://93.184.216.34/page";
        const string image = "http://93.184.216.34/broken.png";
        var html = $"<html><body><p>Intro.</p><img src=\"{image}\" alt=\"a diagram\" /></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        var handler = new ImageAwareStubHandler(new() { [root] = html }, failingUrls: new() { image });
        var captioner = new IngestionImageCaptioner(VisionLlm("unused").Object);
        var job = CreateJob(root, handler, vectorStore.Object, captioner: captioner);

        await job.RunAsync(CancellationToken.None);

        Assert.Single(upserted);
        Assert.Contains("Intro.", upserted[0].Text);
        Assert.Contains("[image: a diagram]", upserted[0].Text);
    }

    [Fact]
    public async Task RunAsync_CrawlLinkedPagesTrue_LinkedPagesAlsoGetTableAndImageConversion()
    {
        const string root = "http://93.184.216.34/";
        const string linked = "http://93.184.216.34/docs";
        var rootHtml = "<html><body><p>Root content.</p><a href=\"/docs\">Docs</a></body></html>";
        var linkedHtml = "<html><body><table><tr><th>K</th></tr><tr><td>V</td></tr></table></body></html>";
        var vectorStore = new Mock<IVectorStoreService>();
        var upserted = new List<VectorDocument>();
        vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted.AddRange(docs))
            .Returns(Task.CompletedTask);

        var handler = new MultiUrlStubHandler(new() { [root] = rootHtml, [linked] = linkedHtml });
        var job = CreateJob(root, handler, vectorStore.Object, crawlLinkedPages: true);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(2, upserted.Count);
        Assert.Contains(upserted, d => d.Text.Contains("| K |") && d.Text.Contains("| V |"));
    }
}
