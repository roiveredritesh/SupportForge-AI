using System.Net;
using HtmlAgilityPack;
using SupportForge.Core;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Fetches a website/URL KB source directly (no graphify dependency) and indexes its visible text
/// into the vector store via <see cref="KbVectorIndexer"/>.
/// </summary>
public sealed class WebsiteIngestionJob : IIngestionJob
{
    private readonly string _url;
    private readonly HttpClient _httpClient;
    private readonly KbVectorIndexer _indexer;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public WebsiteIngestionJob(string projectId, string url, HttpClient httpClient, KbVectorIndexer indexer, IProjectRepository projects)
    {
        ProjectId = projectId;
        _url = url;
        _httpClient = httpClient;
        _indexer = indexer;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        EnsurePublicHttpUrl(_url);

        var html = await _httpClient.GetStringAsync(_url, ct);
        var text = ExtractVisibleText(html);

        await _indexer.IndexAsync(ProjectId, [(_url, text)], ct);
        await KbSourceSync.MarkSyncedAsync(_projects, ProjectId, _url, ct);
    }

    private static string ExtractVisibleText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style")?.ToList() ?? [])
            node.Remove();

        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        return WebUtility.HtmlDecode(body.InnerText).Trim();
    }

    // SSRF guard: this URL comes from persisted project config and is handed straight to our own
    // outbound fetcher, so it must not be allowed to reach loopback, link-local, private, or
    // cloud-metadata addresses (e.g. 169.254.169.254) that a server-side fetch could otherwise be
    // tricked into reaching.
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
