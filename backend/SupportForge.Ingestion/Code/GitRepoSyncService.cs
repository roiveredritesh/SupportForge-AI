using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Configuration;

namespace SupportForge.Ingestion.Code;

public sealed class GitRepoSyncService
{
    private readonly string? _defaultGithubToken;

    public GitRepoSyncService(IConfiguration configuration) => _defaultGithubToken = configuration["GitHub:Token"];

    // token: the calling org's PAT, resolved by the caller (e.g. CodeIngestionJobFactory) from
    // Org.GitHubAccessToken. Falls back to the single deployment-wide GitHub:Token config value
    // when the org has none set, so dev/test setups and orgs that haven't configured a PAT yet
    // keep working.
    // internal + InternalsVisibleTo (see IngestionQueue's matching comment) so tests can assert
    // token precedence directly rather than round-tripping through a real git remote.
    internal CredentialsHandler? CredentialsProviderFor(string? token)
    {
        var effectiveToken = token ?? _defaultGithubToken;
        return string.IsNullOrEmpty(effectiveToken)
            ? null
            : (_url, _user, _cred) => new UsernamePasswordCredentials { Username = effectiveToken, Password = string.Empty };
    }

    // branch: null clones whatever the source's HEAD currently points to (its default branch, or --
    // for a local source repo in detached HEAD -- that exact commit), same as a plain `git clone`
    // with no -b flag.
    public void CloneOrPull(string remoteUrl, string localPath, string? branch = null, string? token = null)
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
