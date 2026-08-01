using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// The one bespoke connector graphify doesn't cover natively: fetches a Confluence page via the
/// REST API and converts its storage-format body to plain markdown so graphify can extract it like
/// any other doc. A 401/403 propagates as <see cref="HttpRequestException"/> so ingestion fails
/// clearly for that source instead of retrying with a stale credential.
/// </summary>
public sealed class ConfluencePageFetcher
{
    private static readonly Regex HtmlTagPattern = new("<[^>]+>", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public ConfluencePageFetcher(HttpClient http, IOptions<ConfluenceOptions> options)
    {
        _http = http;
        if (_http.BaseAddress is null && !string.IsNullOrEmpty(options.Value.BaseUrl))
            _http.BaseAddress = new Uri(options.Value.BaseUrl);
        if (!string.IsNullOrEmpty(options.Value.ApiToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiToken);
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

        var markdown = $"# {title}\n\n{HtmlTagPattern.Replace(html, string.Empty).Trim()}\n";
        return (title, markdown);
    }
}
