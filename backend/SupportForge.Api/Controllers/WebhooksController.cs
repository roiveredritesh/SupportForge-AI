using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;

namespace SupportForge.Api.Controllers;

/// <summary>
/// C4: push-triggered re-ingestion for sources that support it (GitHub, Confluence) -- closes the
/// "no push-triggered re-ingestion" gap from the Gap Analysis. Deliberately not [Authorize]: both
/// providers call these endpoints anonymously and can't attach a bearer token, so authenticity is
/// verified per-endpoint instead (GitHub's HMAC-SHA256 payload signature; Confluence's shared-secret
/// query parameter, since its webhook feature has no signing mechanism of its own).
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IngestionQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IProjectRepository projects, IngestionQueue queue, IServiceProvider services, IConfiguration configuration, ILogger<WebhooksController> logger)
    {
        _projects = projects;
        _queue = queue;
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("github")]
    public async Task<IActionResult> GitHub(CancellationToken ct)
    {
        Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
            rawBody = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var secret = _configuration["GitHubWebhook:Secret"];
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning("GitHub webhook received but GitHubWebhook:Secret is not configured -- rejecting");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!Request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader) ||
            !IsValidSignature(rawBody, signatureHeader.ToString(), secret))
        {
            return Unauthorized();
        }

        // GitHub sends "ping" on webhook setup and other event types depending on subscription --
        // acknowledge (200), don't error, so GitHub doesn't treat this as a delivery failure and retry.
        if (Request.Headers.TryGetValue("X-GitHub-Event", out var eventType) && eventType != "push")
            return Ok();

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException)
        {
            return BadRequest("Malformed JSON payload.");
        }

        if (!payload.TryGetProperty("repository", out var repo)) return Ok();
        var ownerElement = repo.GetProperty("owner");
        var owner = (ownerElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null)
            ?? (ownerElement.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : null);
        var repoName = repo.TryGetProperty("name", out var repoNameProp) ? repoNameProp.GetString() : null;
        if (owner is null || repoName is null) return Ok();

        var allProjects = await _projects.GetAllAsync(ct);
        var matches = allProjects
            .Select(p => (Project: p, Repo: p.Repos.FirstOrDefault(r =>
                string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Repo, repoName, StringComparison.OrdinalIgnoreCase))))
            .Where(m => m.Repo is not null)
            .ToList();

        var enqueued = 0;
        foreach (var (project, matchedRepo) in matches)
        {
            if (_queue.IsBusy(project.Id))
            {
                _logger.LogInformation(
                    "GitHub webhook: skipping project {ProjectId} for {Owner}/{Repo} -- ingestion already running", project.Id, owner, repoName);
                continue;
            }

            // Only the pushed repo, not the project's other repos/KB sources -- a code push
            // shouldn't also re-run an unrelated Confluence/Website sync for the same project.
            var pseudoProject = new Project { Id = project.Id, Name = project.Name, OrgId = project.OrgId, Repos = new List<GitHubRepoConfig> { matchedRepo! } };
            foreach (var factory in _services.GetServices<IIngestionJobFactory>())
                foreach (var job in factory.CreateJobs(pseudoProject))
                    _queue.Enqueue(job);
            enqueued++;
        }

        _logger.LogInformation(
            "GitHub webhook: {Owner}/{Repo} push enqueued re-ingestion for {Count} project(s)", owner, repoName, enqueued);
        return Accepted();
    }

    /// <summary>
    /// C4 follow-up: Confluence's built-in webhook feature (Cloud admin UI, or a Server/DC plugin)
    /// has no payload-signing mechanism like GitHub's -- verification is a shared secret passed in
    /// the URL's query string instead, since that's configurable from Confluence's plain "target URL"
    /// field without needing custom-header support.
    /// </summary>
    [HttpPost("confluence")]
    public async Task<IActionResult> Confluence([FromQuery] string? secret, CancellationToken ct)
    {
        var configuredSecret = _configuration["ConfluenceWebhook:Secret"];
        if (string.IsNullOrEmpty(configuredSecret))
        {
            _logger.LogWarning("Confluence webhook received but ConfluenceWebhook:Secret is not configured -- rejecting");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrEmpty(secret) ||
            secret.Length != configuredSecret.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(configuredSecret)))
        {
            return Unauthorized();
        }

        JsonElement payload;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            payload = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync(ct));
        }
        catch (JsonException)
        {
            return BadRequest("Malformed JSON payload.");
        }

        // "page.id" (Confluence Cloud's shape) or a bare top-level "pageId" (some Server/DC plugin
        // configurations) -- accept either rather than failing on a shape mismatch.
        string? pageId = null;
        if (payload.TryGetProperty("page", out var page) && page.TryGetProperty("id", out var pageIdProp))
            pageId = pageIdProp.ValueKind == JsonValueKind.String ? pageIdProp.GetString() : pageIdProp.GetRawText();
        else if (payload.TryGetProperty("pageId", out var topLevelPageIdProp))
            pageId = topLevelPageIdProp.ValueKind == JsonValueKind.String ? topLevelPageIdProp.GetString() : topLevelPageIdProp.GetRawText();

        if (pageId is null) return Ok();

        var allProjects = await _projects.GetAllAsync(ct);
        var matches = allProjects
            .Select(p => (Project: p, Source: p.KbSources.FirstOrDefault(s =>
                s.Type == KbSourceType.Confluence && s.Location == pageId)))
            .Where(m => m.Source is not null)
            .ToList();

        var enqueued = 0;
        foreach (var (project, matchedSource) in matches)
        {
            if (_queue.IsBusy(project.Id))
            {
                _logger.LogInformation(
                    "Confluence webhook: skipping project {ProjectId} for page {PageId} -- ingestion already running", project.Id, pageId);
                continue;
            }

            // Only the changed page, not the project's other KB sources or repos.
            var pseudoProject = new Project { Id = project.Id, Name = project.Name, OrgId = project.OrgId, KbSources = new List<KbSourceConfig> { matchedSource! } };
            foreach (var factory in _services.GetServices<IIngestionJobFactory>())
                foreach (var job in factory.CreateJobs(pseudoProject))
                    _queue.Enqueue(job);
            enqueued++;
        }

        _logger.LogInformation("Confluence webhook: page {PageId} update enqueued re-ingestion for {Count} project(s)", pageId, enqueued);
        return Accepted();
    }

    private static bool IsValidSignature(string payload, string signatureHeader, string secret)
    {
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.Ordinal)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();
        var providedHex = signatureHeader[prefix.Length..];

        // Constant-time comparison -- a signature check is a security boundary, not just validation.
        return expectedHex.Length == providedHex.Length &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedHex), Encoding.UTF8.GetBytes(providedHex));
    }
}
