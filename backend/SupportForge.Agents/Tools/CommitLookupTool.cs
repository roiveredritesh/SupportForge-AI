using System.Text.Json;
using LibGit2Sharp;
using SupportForge.Core.Entities;

namespace SupportForge.Agents.Tools;

// U11: recent commit history for a matched code file, plus best-effort PR/issue link resolution.
public sealed record CommitInfo(string Sha, string Author, DateTimeOffset Date, string Message, string? PrUrl);

// U11: local `git log` for a file (LibGit2Sharp, same package GitRepoSyncService already uses) plus
// a standalone GitHub REST call to resolve the PR a commit shipped in -- commit messages don't
// reliably carry a "#123" reference, but GitHub's "list pull requests associated with a commit"
// endpoint always knows. This is a plain HttpClient call, not routed through any Integration
// Hub/MCP mechanism (that's Sprint 3's job) -- keeps this sprint's diff small.
public sealed class CommitLookupTool
{
    private readonly HttpClient _http;
    private readonly string? _githubToken;
    private readonly string _repoCacheRoot;

    public CommitLookupTool(HttpClient http, string? githubToken, string repoCacheRoot)
    {
        _http = http;
        _githubToken = githubToken;
        _repoCacheRoot = repoCacheRoot;
    }

    // Same local-clone path convention CodeIngestionJobFactory uses:
    // {repoCacheRoot}/{projectId}/{repoName}.
    private string RepoPath(string projectId, string repoName) => Path.Combine(_repoCacheRoot, projectId, repoName);

    // ponytail: first configured repo only -- multi-repo file->repo attribution needs a real index
    // (which file lives in which clone), revisit if projects with >1 repo become common.
    public async Task<IReadOnlyList<CommitInfo>> LookupForProjectAsync(
        Project project, string relativeFilePath, int maxCount = 5, CancellationToken ct = default)
    {
        var repo = project.Repos.FirstOrDefault();
        if (repo is null) return [];

        return await LookupAsync(RepoPath(project.Id, repo.Repo), repo.Owner, repo.Repo, relativeFilePath, maxCount, ct);
    }

    // Combines local git log with best-effort PR resolution. A GitHub REST failure (rate limit,
    // network) never fails this call -- entries just carry PrUrl = null, same fault-isolation
    // pattern the rest of the pipeline uses (agents catch/log and continue, never half-crash a
    // response over one optional enrichment).
    public async Task<IReadOnlyList<CommitInfo>> LookupAsync(
        string repoPath, string owner, string repo, string relativeFilePath, int maxCount = 5, CancellationToken ct = default)
    {
        var commits = GetRecentCommits(repoPath, relativeFilePath, maxCount);
        if (commits.Count == 0) return commits;

        var withPrs = new List<CommitInfo>(commits.Count);
        foreach (var c in commits)
        {
            var prUrl = await ResolvePullRequestUrlAsync(owner, repo, c.Sha, ct);
            withPrs.Add(c with { PrUrl = prUrl });
        }
        return withPrs;
    }

    // ponytail: caps how deep into history a single lookup walks -- this is "recent commits for a
    // support answer", not a full blame, so a bounded scan is the right ceiling; revisit if a file's
    // most recent touch routinely sits deeper than this.
    private const int MaxCommitsScanned = 200;

    // git log for one file, most-recent first. Diffs each commit against its first parent instead of
    // Commits.QueryBy(path) (LibGit2Sharp's rename-following file-history walk) -- QueryBy throws a
    // KeyNotFoundException on some small/linear repos (LibGit2Sharp issue with its follow-renames
    // algorithm), and this pipeline only needs "did this commit touch this file", not rename tracking.
    public IReadOnlyList<CommitInfo> GetRecentCommits(string repoPath, string relativeFilePath, int maxCount = 5)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git"))) return [];

        using var repo = new Repository(repoPath);
        var results = new List<CommitInfo>();
        // Explicit descending sort, not reliance on repo.Commits' own enumeration order -- git commit
        // times only have second resolution, so commits made in quick succession (as in a test, or a
        // scripted bulk import) can tie, and this pipeline needs a deterministic "most recent first".
        foreach (var commit in repo.Commits.OrderByDescending(c => c.Author.When).Take(MaxCommitsScanned))
        {
            var parent = commit.Parents.FirstOrDefault();
            var touchedFile = parent is null
                ? commit.Tree[relativeFilePath] is not null
                : repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree)
                    .Any(c => c.Path == relativeFilePath || c.OldPath == relativeFilePath);

            if (!touchedFile) continue;

            results.Add(new CommitInfo(commit.Sha, commit.Author.Name, commit.Author.When, commit.MessageShort, null));
            if (results.Count >= maxCount) break;
        }
        return results;
    }

    // GET /repos/{owner}/{repo}/commits/{sha}/pulls -- GitHub's own commit->PR association, more
    // reliable than parsing "#123" out of commit messages. Any failure (network, non-2xx, malformed
    // body) degrades to null rather than throwing, so the caller always keeps its local git log data.
    public async Task<string?> ResolvePullRequestUrlAsync(string owner, string repo, string sha, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/commits/{sha}/pulls");
            request.Headers.Add("User-Agent", "SupportForge");
            request.Headers.Add("Accept", "application/vnd.github+json");
            if (!string.IsNullOrEmpty(_githubToken))
                request.Headers.Add("Authorization", $"Bearer {_githubToken}");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var prs = doc.RootElement;
            if (prs.ValueKind != JsonValueKind.Array || prs.GetArrayLength() == 0) return null;

            return prs[0].TryGetProperty("html_url", out var url) ? url.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
