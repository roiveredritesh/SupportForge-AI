using System.Net;
using System.Text;
using LibGit2Sharp;
using SupportForge.Agents.Tools;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

// U11: real commit history (via a real local LibGit2Sharp repo, mirroring
// GitRepoSyncServiceTests' own pattern) + a fake HttpMessageHandler standing in for the GitHub REST
// call, so PR-link resolution and its failure path are both exercised without a live network call.
public class CommitLookupToolTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static string CreateRepoWithCommits(string fileName, params string[] messages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "commitlookup-" + Guid.NewGuid());
        Repository.Init(dir);
        using var repo = new Repository(dir);
        // Git commit times only have second resolution -- commits made back-to-back in a test would
        // otherwise tie, so each gets an explicit, strictly increasing timestamp.
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-messages.Length);

        for (var i = 0; i < messages.Length; i++)
        {
            var sig = new Signature("Jane Doe", "jane@example.com", baseTime.AddMinutes(i));
            File.WriteAllText(Path.Combine(dir, fileName), messages[i]);
            Commands.Stage(repo, fileName);
            repo.Commit(messages[i], sig, sig);
        }

        return dir;
    }

    private static void Cleanup(string dir)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void GetRecentCommits_WithRealCommitHistory_ReturnsAuthorDateAndMessage_MostRecentFirst()
    {
        var dir = CreateRepoWithCommits("Foo.cs", "initial version", "fix null check", "add logging");
        try
        {
            var tool = new CommitLookupTool(new HttpClient(), null, Path.GetTempPath());

            var commits = tool.GetRecentCommits(dir, "Foo.cs", maxCount: 5);

            Assert.Equal(3, commits.Count);
            Assert.Equal("add logging", commits[0].Message);
            Assert.Equal("fix null check", commits[1].Message);
            Assert.Equal("initial version", commits[2].Message);
            Assert.All(commits, c => Assert.Equal("Jane Doe", c.Author));
            Assert.All(commits, c => Assert.False(string.IsNullOrEmpty(c.Sha)));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetRecentCommits_RespectsMaxCount()
    {
        var dir = CreateRepoWithCommits("Foo.cs", "one", "two", "three", "four");
        try
        {
            var tool = new CommitLookupTool(new HttpClient(), null, Path.GetTempPath());

            var commits = tool.GetRecentCommits(dir, "Foo.cs", maxCount: 2);

            Assert.Equal(2, commits.Count);
            Assert.Equal("four", commits[0].Message);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void GetRecentCommits_WhenPathIsNotAGitRepo_ReturnsEmpty_WithoutThrowing()
    {
        var tool = new CommitLookupTool(new HttpClient(), null, Path.GetTempPath());

        var commits = tool.GetRecentCommits(Path.Combine(Path.GetTempPath(), "not-a-repo-" + Guid.NewGuid()), "Foo.cs");

        Assert.Empty(commits);
    }

    [Fact]
    public async Task ResolvePullRequestUrlAsync_WhenGitHubReturnsAPr_ReturnsItsHtmlUrl()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"number": 42, "html_url": "https://github.com/acme/widget/pull/42"}]""",
                Encoding.UTF8, "application/json"),
        });
        var tool = new CommitLookupTool(new HttpClient(handler), "gh-token", Path.GetTempPath());

        var url = await tool.ResolvePullRequestUrlAsync("acme", "widget", "abc123");

        Assert.Equal("https://github.com/acme/widget/pull/42", url);
        Assert.Contains("Bearer gh-token", handler.LastRequest!.Headers.GetValues("Authorization"));
    }

    [Fact]
    public async Task ResolvePullRequestUrlAsync_WhenGitHubReturnsNoPrs_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var tool = new CommitLookupTool(new HttpClient(handler), null, Path.GetTempPath());

        var url = await tool.ResolvePullRequestUrlAsync("acme", "widget", "abc123");

        Assert.Null(url);
    }

    // U11: rate limit / network failure -- the tool must degrade, not throw, so the caller still gets
    // local git log data (LookupAsync's own test below covers the combined path).
    [Fact]
    public async Task ResolvePullRequestUrlAsync_WhenGitHubCallFails_ReturnsNull_WithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message": "API rate limit exceeded"}""", Encoding.UTF8, "application/json"),
        });
        var tool = new CommitLookupTool(new HttpClient(handler), null, Path.GetTempPath());

        var url = await tool.ResolvePullRequestUrlAsync("acme", "widget", "abc123");

        Assert.Null(url);
    }

    [Fact]
    public async Task ResolvePullRequestUrlAsync_WhenHttpClientThrows_ReturnsNull_WithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("network unreachable"));
        var tool = new CommitLookupTool(new HttpClient(handler), null, Path.GetTempPath());

        var url = await tool.ResolvePullRequestUrlAsync("acme", "widget", "abc123");

        Assert.Null(url);
    }

    [Fact]
    public async Task LookupAsync_CombinesLocalCommitsWithPrLinks_AndDegradesGracefullyOnGitHubFailure()
    {
        var dir = CreateRepoWithCommits("Foo.cs", "fix the bug");
        try
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var tool = new CommitLookupTool(new HttpClient(handler), null, Path.GetTempPath());

            var commits = await tool.LookupAsync(dir, "acme", "widget", "Foo.cs");

            Assert.Single(commits);
            Assert.Equal("fix the bug", commits[0].Message);
            Assert.Null(commits[0].PrUrl); // GitHub call failed -- local data still comes back
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LookupForProjectAsync_WithNoRepos_ReturnsEmpty()
    {
        var tool = new CommitLookupTool(new HttpClient(), null, Path.GetTempPath());
        var project = new Project { Id = "proj1", Name = "Proj" };

        var commits = await tool.LookupForProjectAsync(project, "Foo.cs");

        Assert.Empty(commits);
    }

    [Fact]
    public async Task LookupForProjectAsync_UsesFirstRepoAndCacheRootConvention_ToLocateTheClone()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "cacheroot-" + Guid.NewGuid());
        var repoDir = Path.Combine(cacheRoot, "proj1", "widget");
        Directory.CreateDirectory(Path.GetDirectoryName(repoDir)!);
        Repository.Init(repoDir);
        using (var repo = new Repository(repoDir))
        {
            File.WriteAllText(Path.Combine(repoDir, "Foo.cs"), "content");
            Commands.Stage(repo, "Foo.cs");
            var sig = new Signature("Jane Doe", "jane@example.com", DateTimeOffset.UtcNow);
            repo.Commit("add Foo.cs", sig, sig);
        }

        try
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
            var tool = new CommitLookupTool(new HttpClient(handler), null, cacheRoot);
            var project = new Project
            {
                Id = "proj1",
                Name = "Proj",
                Repos = [new GitHubRepoConfig("acme", "widget", "main")],
            };

            var commits = await tool.LookupForProjectAsync(project, "Foo.cs");

            Assert.Single(commits);
            Assert.Equal("add Foo.cs", commits[0].Message);
        }
        finally { Cleanup(cacheRoot); }
    }
}
