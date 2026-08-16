using System.Net;
using HtmlAgilityPack;
using SupportForge.Core;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Fetches a website/URL KB source directly and indexes its visible text into the vector store via
/// <see cref="KbVectorIndexer"/>. When <see cref="_crawlLinkedPages"/> is set, also indexes same-host
/// pages linked directly from the root page (one level, not a recursive site crawl -- see the
/// ponytail note on <see cref="MaxCrawledPages"/>).
/// </summary>
public sealed class WebsiteIngestionJob : IIngestionJob
{
    // ponytail: single-level link expansion (root page's own links only, not a recursive
    // multi-hop crawl), capped so a large site can't turn one ingestion run into hundreds of
    // fetches. Add recursive depth + a visited-page budget if a real KB source needs deeper
    // coverage than "root page + its direct links".
    private const int MaxCrawledPages = 25;

    private readonly string _url;
    private readonly bool _crawlLinkedPages;
    private readonly HttpClient _httpClient;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;
    private readonly IngestionImageCaptioner _captioner;
    private readonly string? _triggeredByUserId;

    public string ProjectId { get; }

    public WebsiteIngestionJob(
        string projectId, string url, HttpClient httpClient, KbVectorIndexer indexer, IProjectRepository projects,
        IngestionImageCaptioner captioner, bool crawlLinkedPages = false, string? triggeredByUserId = null)
    {
        ProjectId = projectId;
        _url = url;
        _crawlLinkedPages = crawlLinkedPages;
        _httpClient = httpClient;
        _indexer = indexer;
        _projects = projects;
        _captioner = captioner;
        _triggeredByUserId = triggeredByUserId;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        EnsurePublicHttpUrl(_url);

        var rootDoc = await FetchAsync(_url, ct);
        var pages = new List<(string SourceRef, string Text, string? Title)> { (_url, await ExtractContentAsync(rootDoc, ct), ExtractTitle(rootDoc)) };

        if (_crawlLinkedPages)
        {
            var rootUri = new Uri(_url);
            foreach (var link in DiscoverSameHostLinks(rootDoc, rootUri).Take(MaxCrawledPages))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    EnsurePublicHttpUrl(link);
                    var linkedDoc = await FetchAsync(link, ct);
                    pages.Add((link, await ExtractContentAsync(linkedDoc, ct), ExtractTitle(linkedDoc)));
                }
                catch (Exception ex) when (ex is HttpRequestException or ArgumentException or TaskCanceledException)
                {
                    // One broken, disallowed, or slow linked page shouldn't fail the whole crawl --
                    // the root page (and every other linked page) still gets indexed.
                }
            }
        }

        await _indexer.IndexAsync(ProjectId, pages, ct, _triggeredByUserId);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _url, ct);
    }

    private async Task<HtmlDocument> FetchAsync(string url, CancellationToken ct)
    {
        var html = await _httpClient.GetStringAsync(url, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    // Converts the page body to Markdown (tables, images -- see HtmlToMarkdownConverter) instead of
    // flattening it to InnerText (R2/R3). Converted separately from a body-only sub-document (rather
    // than the whole HtmlDocument) so <title>/<head> text -- already captured by ExtractTitle -- isn't
    // also walked into the indexed body content.
    private async Task<string> ExtractContentAsync(HtmlDocument doc, CancellationToken ct)
    {
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        var bodyDoc = new HtmlDocument();
        bodyDoc.LoadHtml(body.InnerHtml);

        var converted = HtmlToMarkdownConverter.Convert(bodyDoc);
        var markdown = converted.Markdown;
        foreach (var token in converted.Images)
        {
            var caption = await _captioner.CaptionAsync(token, resolveCt => ResolveImageBytesAsync(token, resolveCt), ct);
            markdown = markdown.Replace(ImageToken(token), caption);
        }

        return WebUtility.HtmlDecode(markdown).Trim();
    }

    // Must exactly match HtmlToMarkdownConverter's private FormatImageToken so the substring
    // substitution above actually finds the token it's replacing.
    private static string ImageToken(ImageCaptionCandidate token) =>
        "{{IMAGE:" + (token.Kind == ImageSourceKind.Attachment ? "attachment" : "external") + ":" + token.SourceRef + "|" + (token.AltOrName ?? token.SourceRef) + "}}";

    // Every image token this job ever sees is External (plain <img src>) -- SSRF-checked with the
    // same guard already applied to crawled page links, then fetched through this job's own
    // HttpClient before any bytes reach the captioner.
    private async Task<byte[]> ResolveImageBytesAsync(ImageCaptionCandidate token, CancellationToken ct)
    {
        EnsurePublicHttpUrl(token.SourceRef);
        var response = await _httpClient.GetAsync(token.SourceRef, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // D1 (gap-closing-solutions.md Phase D, item 1): <title> was already being parsed away as part
    // of the page and discarded -- attaching it as chunk metadata was previously free information
    // left on the floor.
    private static string? ExtractTitle(HtmlDocument doc)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        var title = titleNode?.InnerText is { } t ? WebUtility.HtmlDecode(t).Trim() : null;
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    // Same-host only (not "same domain including subdomains") -- a link to a different host is a
    // different site's content, which this KB source was never configured to index.
    private static IEnumerable<string> DiscoverSameHostLinks(HtmlDocument doc, Uri rootUri)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeForDedup(rootUri) };
        var hrefs = doc.DocumentNode.SelectNodes("//a[@href]")?.Select(n => n.GetAttributeValue("href", "")) ?? [];

        foreach (var href in hrefs)
        {
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(rootUri, href, out var resolved)) continue;
            if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) continue;
            if (!string.Equals(resolved.Host, rootUri.Host, StringComparison.OrdinalIgnoreCase)) continue;

            var key = NormalizeForDedup(resolved);
            if (!seen.Add(key)) continue;

            yield return resolved.GetLeftPart(UriPartial.Query);
        }
    }

    // Strips the fragment (#section) so "/page" and "/page#section" aren't crawled as two pages.
    private static string NormalizeForDedup(Uri uri) => uri.GetLeftPart(UriPartial.Query);

    // SSRF guard: this URL comes from persisted project config and is handed straight to our own
    // outbound fetcher, so it must not be allowed to reach loopback, link-local, private, or
    // cloud-metadata addresses (e.g. 169.254.169.254) that a server-side fetch could otherwise be
    // tricked into reaching. Applied to every discovered link too, not just the configured root URL.
    private static void EnsurePublicHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Website KB source URL '{url}' must be an absolute http(s) URL.", nameof(url));

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(uri.Host);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new ArgumentException($"Website KB source URL '{url}' host '{uri.Host}' could not be resolved.", nameof(url));
        }

        foreach (var address in addresses)
        {
            if (IsDisallowedAddress(address))
                throw new ArgumentException(
                    $"Website KB source URL '{url}' resolves to a non-public address ('{address}') and is not allowed.", nameof(url));
        }
    }

    private static bool IsDisallowedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 (includes cloud metadata 169.254.169.254)
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fc00::/7 unique local, fe80::/10 link-local
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return false;
    }
}
