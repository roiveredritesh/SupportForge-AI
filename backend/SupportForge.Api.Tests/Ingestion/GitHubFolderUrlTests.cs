using SupportForge.Ingestion.Documents;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GitHubFolderUrlTests
{
    [Fact]
    public void TryParse_TreeUrlWithSubpath_ExtractsOwnerRepoBranchSubPath()
    {
        var ok = GitHubFolderUrl.TryParse("https://github.com/acme/widgets/tree/develop/docs/guides", out var result);

        Assert.True(ok);
        Assert.Equal("acme", result!.Owner);
        Assert.Equal("widgets", result.Repo);
        Assert.Equal("develop", result.Branch);
        Assert.Equal("docs/guides", result.SubPath);
    }

    [Fact]
    public void TryParse_BareRepoUrl_DefaultsToMainBranchAndEmptySubPath()
    {
        var ok = GitHubFolderUrl.TryParse("https://github.com/acme/widgets", out var result);

        Assert.True(ok);
        Assert.Equal("acme", result!.Owner);
        Assert.Equal("widgets", result.Repo);
        Assert.Equal("main", result.Branch);
        Assert.Equal("", result.SubPath);
    }

    [Fact]
    public void TryParse_StripsGitSuffixFromRepoName()
    {
        var ok = GitHubFolderUrl.TryParse("https://github.com/acme/widgets.git", out var result);

        Assert.True(ok);
        Assert.Equal("widgets", result!.Repo);
    }

    [Theory]
    [InlineData("/standalone/docs")]
    [InlineData("https://example.com/acme/widgets")]
    [InlineData("https://github.com/acme")]
    [InlineData("not a url")]
    public void TryParse_ReturnsFalse_ForNonGitHubFolderLocations(string location)
    {
        var ok = GitHubFolderUrl.TryParse(location, out var result);

        Assert.False(ok);
        Assert.Null(result);
    }
}
