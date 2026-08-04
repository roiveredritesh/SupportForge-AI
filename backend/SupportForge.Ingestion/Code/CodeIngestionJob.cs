using System.Text.Json;
using SupportForge.Core;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJob : IIngestionJob
{
    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localCachePath;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly GitRepoSyncService _gitSync;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public CodeIngestionJob(string projectId, string repoUrl, string branch, string localCachePath,
        string repoOwner, string repoName, GitRepoSyncService gitSync, IProjectRepository projects)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localCachePath = localCachePath;
        _repoOwner = repoOwner;
        _repoName = repoName;
        _gitSync = gitSync;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _gitSync.CloneOrPull(_repoUrl, _localCachePath, _branch);
        Directory.CreateDirectory(_localCachePath);

        // GraphImportJobFactory reads this same path (<repo>/code-graph/graph.json) and loads it into
        // Neo4j tagged with projectId -- GraphImportJob runs as a separate, later-registered job so
        // this job doesn't need a Neo4j driver of its own.
        var graph = CodeGraphExtractor.Extract(_localCachePath);
        var graphOutDir = Path.Combine(_localCachePath, "code-graph");
        Directory.CreateDirectory(graphOutDir);
        await File.WriteAllTextAsync(Path.Combine(graphOutDir, "graph.json"), JsonSerializer.Serialize(graph), ct);

        var project = await _projects.GetByIdAsync(ProjectId, ct);
        if (project != null)
        {
            var repoIndex = project.Repos.FindIndex(r => r.Owner == _repoOwner && r.Repo == _repoName);
            if (repoIndex >= 0)
            {
                project.Repos[repoIndex] = project.Repos[repoIndex] with { LastSyncedAt = DateTimeOffset.UtcNow };
                await _projects.UpsertAsync(project, ct);
            }
        }
    }
}
