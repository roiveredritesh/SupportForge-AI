using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using SupportForge.Ingestion.Code;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GitRepoSyncServiceTests
{
    [Fact]
    public void CloneOrPull_ClonesFreshRepo_WhenLocalPathDoesNotExist()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), "remote-" + Guid.NewGuid());
        var localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid());
        Repository.Init(remoteDir, isBare: true);

        var seedDir = Path.Combine(Path.GetTempPath(), "seed-" + Guid.NewGuid());
        Repository.Clone(remoteDir, seedDir);
        File.WriteAllText(Path.Combine(seedDir, "readme.md"), "hello");
        using (var seedRepo = new Repository(seedDir))
        {
            Commands.Stage(seedRepo, "*");
            var sig = new Signature("test", "test@test.com", DateTimeOffset.Now);
            seedRepo.Commit("initial commit", sig, sig);
            seedRepo.Network.Push(seedRepo.Branches["master"]);
        }

        var sut = new GitRepoSyncService(new ConfigurationBuilder().Build());
        sut.CloneOrPull(remoteDir, localDir, "master");

        Assert.True(File.Exists(Path.Combine(localDir, "readme.md")));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        System.Threading.Thread.Sleep(100);

        try { Directory.Delete(remoteDir, recursive: true); } catch { }
        try { Directory.Delete(localDir, recursive: true); } catch { }
        try { Directory.Delete(seedDir, recursive: true); } catch { }
    }
}
