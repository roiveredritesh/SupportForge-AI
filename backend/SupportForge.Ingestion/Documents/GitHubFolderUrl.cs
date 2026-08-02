namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Parses a github.com URL pasted into a <c>Documents</c> KB source's Location into the repo/branch/
/// subpath needed to clone it. Supports "https://github.com/{owner}/{repo}" (whole repo, default
/// branch assumed "main" -- the URL alone doesn't carry the repo's actual default branch) and
/// "https://github.com/{owner}/{repo}/tree/{branch}/{subpath...}" (specific branch/folder).
/// </summary>
public sealed record GitHubFolderUrl(string Owner, string Repo, string Branch, string SubPath)
{
    public static bool TryParse(string location, out GitHubFolderUrl? result)
    {
        result = null;
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];

        if (segments.Length >= 4 && segments[2] == "tree")
        {
            result = new GitHubFolderUrl(owner, repo, segments[3], string.Join('/', segments.Skip(4)));
            return true;
        }

        if (segments.Length == 2)
        {
            result = new GitHubFolderUrl(owner, repo, "main", "");
            return true;
        }

        return false;
    }
}
