using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Fetches a Confluence page via the REST API and converts its storage-format body to Markdown
/// (tables, images/diagrams captioned via <see cref="IngestionImageCaptioner"/>) so it can be
/// indexed like any other doc. A 401/403 propagates as <see cref="HttpRequestException"/> so
/// ingestion fails clearly for that source instead of retrying with a stale credential.
/// </summary>
public sealed class ConfluencePageFetcher
{
    // Strips <script>/<style> elements (tags AND their content) before conversion. HtmlToMarkdownConverter
    // does this too internally, but running it here as well means any prompt-injection text an editor
    // hid in a script/style block never even reaches the HTML parser as node content.
    private static readonly Regex ScriptOrStylePattern = new(
        @"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly HttpClient _externalImagesHttp;
    private readonly IngestionImageCaptioner _captioner;

    public ConfluencePageFetcher(
        HttpClient http,
        IOptions<ConfluenceOptions> options,
        IngestionImageCaptioner captioner,
        IHttpClientFactory httpClientFactory)
    {
        _http = http;
        if (_http.BaseAddress is null && !string.IsNullOrEmpty(options.Value.BaseUrl))
            _http.BaseAddress = new Uri(options.Value.BaseUrl);
        if (!string.IsNullOrEmpty(options.Value.ApiToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiToken);

        _captioner = captioner;
        // Deliberately a separate, credential-free client (KTD10): external image/diagram URLs are
        // editor-supplied (ri:url / <img src>) and must never carry the Confluence bearer token.
        _externalImagesHttp = httpClientFactory.CreateClient("ConfluenceExternalImages");
    }

    public async Task<(string Title, string Markdown)> FetchPageAsMarkdownAsync(string pageId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/rest/api/content/{pageId}?expand=body.storage", ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? pageId : pageId;
        var html = root.TryGetProperty("body", out var bodyEl)
            && bodyEl.TryGetProperty("storage", out var storageEl)
            && storageEl.TryGetProperty("value", out var valueEl)
                ? valueEl.GetString() ?? string.Empty
                : string.Empty;

        var withoutScriptsOrStyles = ScriptOrStylePattern.Replace(html, string.Empty);

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(withoutScriptsOrStyles);
        var converted = HtmlToMarkdownConverter.Convert(htmlDoc);

        var markdown = converted.Markdown;
        foreach (var token in converted.Images)
        {
            var caption = await _captioner.CaptionAsync(token, resolveCt => ResolveImageBytesAsync(token, resolveCt), ct);
            markdown = markdown.Replace(ImageToken(token), caption);
        }

        var decoded = WebUtility.HtmlDecode(markdown);
        var result = $"# {title}\n\n{decoded.Trim()}\n";
        return (title, result);
    }

    // Must exactly match HtmlToMarkdownConverter's private FormatImageToken so the substring
    // substitution below actually finds the token it's replacing.
    private static string ImageToken(ImageCaptionCandidate token) =>
        "{{IMAGE:" + (token.Kind == ImageSourceKind.Attachment ? "attachment" : "external") + ":" + token.SourceRef + "|" + (token.AltOrName ?? token.SourceRef) + "}}";

    private async Task<byte[]> ResolveImageBytesAsync(ImageCaptionCandidate token, CancellationToken ct)
    {
        if (token.Kind == ImageSourceKind.Attachment)
        {
            // Fixed path on _http's own BaseAddress -- carries the same Confluence Authorization
            // header already set on _http, safe because the attachment id is Confluence-internal and
            // this always targets Confluence's own host.
            var response = await _http.GetAsync($"/rest/api/content/{token.SourceRef}/download", ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        // External (editor-supplied ri:url / <img src>): SSRF-check before any fetch attempt, then
        // fetch via the credential-free client so the Confluence bearer token is never attached.
        EnsurePublicHttpUrl(token.SourceRef);
        var externalResponse = await _externalImagesHttp.GetAsync(token.SourceRef, ct);
        externalResponse.EnsureSuccessStatusCode();
        return await externalResponse.Content.ReadAsByteArrayAsync(ct);
    }

    // SSRF guard, equivalent to WebsiteIngestionJob.EnsurePublicHttpUrl/IsDisallowedAddress: this URL
    // comes from editor-supplied page content and must not be allowed to reach loopback, link-local,
    // private, or cloud-metadata addresses (e.g. 169.254.169.254).
    private static void EnsurePublicHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"Confluence external image URL '{url}' must be an absolute http(s) URL.", nameof(url));

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(uri.Host);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ArgumentException($"Confluence external image URL '{url}' host '{uri.Host}' could not be resolved.", nameof(url));
        }

        foreach (var address in addresses)
        {
            if (IsDisallowedAddress(address))
                throw new ArgumentException(
                    $"Confluence external image URL '{url}' resolves to a non-public address ('{address}') and is not allowed.", nameof(url));
        }
    }

    private static bool IsDisallowedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 (includes cloud metadata 169.254.169.254)
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 unique local, fe80::/10 link-local
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }
        return false;
    }
}
