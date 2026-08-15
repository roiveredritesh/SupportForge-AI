using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Configuration;
using SupportForge.Core;

namespace SupportForge.Ingestion.Code;

public sealed class GitRepoSyncService
{
    private readonly string? _githubToken;
    private readonly IOrgRepository? _orgs;

    // orgs is optional (defaults to null) so existing callers/tests that only need the global
    // GitHub:Token fallback (no org-aware overload) don't need to supply one.
    public GitRepoSyncService(IConfiguration configuration, IOrgRepository? orgs = null)
    {
        _githubToken = configuration["GitHub:Token"];
        _orgs = orgs;
    }

    private static CredentialsHandler? CredentialsProviderFor(string? token) =>
        string.IsNullOrEmpty(token)
            ? null
            : (_url, _user, _cred) => new UsernamePasswordCredentials { Username = token, Password = string.Empty };

    // Sprint 3 (U15): a connected GitHub MCP connection on the given org takes precedence over the
    // global GitHub:Token config, which remains the fallback default -- existing single-org
    // deployments with no MCP connection configured keep working unmodified. internal +
    // InternalsVisibleTo (see IngestionQueue's matching comment) so this precedence rule is
    // directly testable without needing a real git remote to observe which token got used.
    internal async Task<string?> ResolveTokenAsync(string? orgId, CancellationToken ct = default)
    {
        if (orgId is not null && _orgs is not null)
        {
            var org = await _orgs.GetByIdAsync(orgId, ct);
            var githubConnection = org?.Connections.FirstOrDefault(c => c.ServerType == "github");
            if (githubConnection is not null) return githubConnection.Credential;
        }
        return _githubToken;
    }

    // branch: null clones whatever the source's HEAD currently points to (its default branch, or --
    // for a local source repo in detached HEAD -- that exact commit), same as a plain `git clone`
    // with no -b flag.
    public void CloneOrPull(string remoteUrl, string localPath, string? branch = null) =>
        CloneOrPullWithToken(remoteUrl, localPath, branch, _githubToken);

    // orgId: null skips the org lookup and uses the global GitHub:Token directly (same as CloneOrPull).
    public async Task CloneOrPullAsync(string remoteUrl, string localPath, string? branch, string? orgId, CancellationToken ct = default) =>
        CloneOrPullWithToken(remoteUrl, localPath, branch, await ResolveTokenAsync(orgId, ct));

    private static void CloneOrPullWithToken(string remoteUrl, string localPath, string? branch, string? token)
    {
        var credentialsProvider = CredentialsProviderFor(token);

        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            var options = new CloneOptions { BranchName = branch };
            options.FetchOptions.CredentialsProvider = credentialsProvider;
            Repository.Clone(remoteUrl, localPath, options);
            return;
        }

        using var repo = new Repository(localPath);
        var pullOptions = new PullOptions { FetchOptions = new FetchOptions { CredentialsProvider = credentialsProvider } };
        Commands.Pull(repo, new Signature("supportforge", "supportforge@internal", DateTimeOffset.Now), pullOptions);
    }
}
