using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Configuration;

namespace SupportForge.Ingestion.Code;

public sealed class GitRepoSyncService
{
    private readonly string? _githubToken;

    public GitRepoSyncService(IConfiguration configuration) => _githubToken = configuration["GitHub:Token"];

    private CredentialsHandler? CredentialsProvider =>
        string.IsNullOrEmpty(_githubToken)
            ? null
            : (_url, _user, _cred) => new UsernamePasswordCredentials { Username = _githubToken, Password = string.Empty };

    // branch: null clones whatever the source's HEAD currently points to (its default branch, or --
    // for a local source repo in detached HEAD -- that exact commit), same as a plain `git clone`
    // with no -b flag.
    public void CloneOrPull(string remoteUrl, string localPath, string? branch = null)
    {
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            var options = new CloneOptions { BranchName = branch };
            options.FetchOptions.CredentialsProvider = CredentialsProvider;
            Repository.Clone(remoteUrl, localPath, options);
            return;
        }

        using var repo = new Repository(localPath);
        var pullOptions = new PullOptions { FetchOptions = new FetchOptions { CredentialsProvider = CredentialsProvider } };
        Commands.Pull(repo, new Signature("supportforge", "supportforge@internal", DateTimeOffset.Now), pullOptions);
    }
}
