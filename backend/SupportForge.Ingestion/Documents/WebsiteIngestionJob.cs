using System.Net;
using SupportForge.Core;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Folds a website/URL KB source into the project's graph via `graphify add &lt;url&gt;`, which
/// fetches the page into `./raw` and updates the graph in place -- no bespoke crawler needed.
/// </summary>
public sealed class WebsiteIngestionJob : IIngestionJob
{
    private readonly string _corpusPath;
    private readonly string _url;
    private readonly GraphifyCliRunner _graphify;
    private readonly IReadOnlyDictionary<string, string?> _graphifyEnvironment;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public WebsiteIngestionJob(
        string projectId, string corpusPath, string url, GraphifyCliRunner graphify,
        IReadOnlyDictionary<string, string?> graphifyEnvironment, IProjectRepository projects)
    {
        ProjectId = projectId;
        _corpusPath = corpusPath;
        _url = url;
        _graphify = graphify;
        _graphifyEnvironment = graphifyEnvironment;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        EnsurePublicHttpUrl(_url);

        Directory.CreateDirectory(_corpusPath);
        await _graphify.RunAsync(_corpusPath, _graphifyEnvironment, ct, "add", _url);

        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project != null)
        {
            var sourceIndex = project.KbSources.FindIndex(s => s.Location == _url);
            if (sourceIndex >= 0)
            {
                project.KbSources[sourceIndex] = project.KbSources[sourceIndex] with { LastSyncedAt = DateTimeOffset.UtcNow };
                await _projects.UpsertAsync(project, ct);
            }
        }
    }

    // SSRF guard: this URL comes from persisted project config and is handed straight to
    // graphify's own outbound fetcher, so it must not be allowed to reach loopback, link-local,
    // private, or cloud-metadata addresses (e.g. 169.254.169.254) that graphify running
    // server-side could otherwise be tricked into fetching.
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
