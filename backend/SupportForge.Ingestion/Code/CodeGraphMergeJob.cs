using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Code;

/// <summary>
/// Merges every one of a project's per-repo <c>graphify-out/graph.json</c> files into a single
/// project-scoped graph so <c>graphify query</c> can answer questions across all of a project's
/// repos at once. <c>graphify merge-graphs</c> requires 2+ inputs -- with exactly one repo, its own
/// graph is copied to the project-level path instead of calling merge-graphs.
/// </summary>
public sealed class CodeGraphMergeJob : IIngestionJob
{
    private readonly IReadOnlyList<string> _repoGraphPaths;
    private readonly string _projectGraphOutPath;
    private readonly GraphifyCliRunner _graphify;

    public string ProjectId { get; }

    public CodeGraphMergeJob(string projectId, IReadOnlyList<string> repoGraphPaths, string projectGraphOutPath, GraphifyCliRunner graphify)
    {
        ProjectId = projectId;
        _repoGraphPaths = repoGraphPaths;
        _projectGraphOutPath = projectGraphOutPath;
        _graphify = graphify;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_projectGraphOutPath)!);

        if (_repoGraphPaths.Count == 1)
        {
            File.Copy(_repoGraphPaths[0], _projectGraphOutPath, overwrite: true);
            return;
        }

        var args = new[] { "merge-graphs" }.Concat(_repoGraphPaths).Concat(["--out", _projectGraphOutPath]).ToArray();
        await _graphify.RunAsync(Directory.GetCurrentDirectory(), environment: null, ct, args);
    }
}
