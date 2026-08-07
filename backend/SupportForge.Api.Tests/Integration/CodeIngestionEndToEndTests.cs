using Microsoft.Extensions.Configuration;
using SupportForge.Ingestion.Code;
using Xunit;

namespace SupportForge.Api.Tests.Integration;

/// <summary>
/// Exercises the real clone -> extract -> graph.json chain (the part of CodeIngestionJob that runs
/// with no external service) against this repo itself, cloned from the local git checkout. Proves
/// CodeGraphExtractor doesn't choke on a real, full-size codebase and produces a plausible graph --
/// not just the small synthetic fixtures in CodeGraphExtractorTests.
/// </summary>
public class CodeIngestionEndToEndTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SupportForge.Backend.sln")))
            dir = dir.Parent;
        return dir?.Parent?.FullName // repo root is one level above the "backend" folder containing the .sln
            ?? throw new InvalidOperationException("Could not locate repo root from test output directory.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CloneAndExtract_OnThisRepository_ProducesGraphWithKnownSymbolsAndFiles()
    {
        var repoRoot = FindRepoRoot();
        var localClonePath = Path.Combine(Path.GetTempPath(), "supportforge-e2e-clone-" + Guid.NewGuid());

        var gitSync = new GitRepoSyncService(new ConfigurationBuilder().Build());
        // No branch pinned -- clone whatever this checkout currently has HEAD on (varies by dev
        // machine/CI run; a hardcoded branch name only exists in whichever worktree wrote this test).
        gitSync.CloneOrPull(repoRoot, localClonePath);

        var graph = CodeGraphExtractor.Extract(Path.Combine(localClonePath, "backend"));

        // File nodes for real source files exist.
        Assert.Contains(graph.Nodes, n => n.Id.EndsWith("CodeGraphExtractor.cs", StringComparison.Ordinal) && n.FileType == "csharp");
        Assert.Contains(graph.Nodes, n => n.Id.EndsWith("GraphDbQueryTool.cs", StringComparison.Ordinal));

        // Definition nodes + "defines" edges for real classes in this codebase.
        Assert.Contains(graph.Nodes, n => n.Label == "CodeGraphExtractor");
        Assert.Contains(graph.Nodes, n => n.Label == "GraphDbQueryTool");
        Assert.Contains(graph.Edges, e => e.Relation == "defines" && e.Target.EndsWith("::CodeGraphExtractor", StringComparison.Ordinal));

        // Sanity floor: this codebase has far more than a handful of classes/files.
        Assert.True(graph.Nodes.Count > 50, $"expected a substantial graph from a real codebase, got {graph.Nodes.Count} nodes");

        DeleteDirectoryEvenIfReadOnly(localClonePath);
    }

    // libgit2 leaves loose objects under .git read-only on Windows; Directory.Delete throws
    // UnauthorizedAccessException on those unless the attribute is cleared first.
    private static void DeleteDirectoryEvenIfReadOnly(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
