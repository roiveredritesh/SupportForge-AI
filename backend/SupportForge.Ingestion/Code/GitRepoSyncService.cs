using LibGit2Sharp;

namespace SupportForge.Ingestion.Code;

public sealed class GitRepoSyncService
{
    public void CloneOrPull(string remoteUrl, string localPath, string branch)
    {
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            Repository.Clone(remoteUrl, localPath, new CloneOptions { BranchName = branch });
            return;
        }

        using var repo = new Repository(localPath);
        Commands.Pull(repo, new Signature("supportforge", "supportforge@internal", DateTimeOffset.Now), new PullOptions());
    }
}
