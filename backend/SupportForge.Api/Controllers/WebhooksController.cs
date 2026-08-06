using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;

namespace SupportForge.Api.Controllers;

/// <summary>
/// C4: GitHub push webhook -- closes the "no push-triggered re-ingestion" gap from the Gap Analysis.
/// Deliberately not [Authorize]: GitHub calls this anonymously and can't attach a bearer token, so
/// authenticity is verified via the HMAC-SHA256 payload signature GitHub itself computes instead.
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
            var pseudoProject = new Project { Id = project.Id, Name = project.Name, Repos = new List<GitHubRepoConfig> { matchedRepo! } };
            foreach (var factory in _services.GetServices<IIngestionJobFactory>())
                foreach (var job in factory.CreateJobs(pseudoProject))
                    _queue.Enqueue(job);
            enqueued++;
        }

        _logger.LogInformation(
            "GitHub webhook: {Owner}/{Repo} push enqueued re-ingestion for {Count} project(s)", owner, repoName, enqueued);
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
