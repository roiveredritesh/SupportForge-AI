using SupportForge.Core;
using SupportForge.Ingestion.Graphify;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJob : IIngestionJob
{
    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localCachePath;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly GitRepoSyncService _gitSync;
    private readonly GraphifyCliRunner _graphify;
    private readonly IProjectRepository _projects;

    public string ProjectId { get; }

    public CodeIngestionJob(string projectId, string repoUrl, string branch, string localCachePath,
        string repoOwner, string repoName, GitRepoSyncService gitSync, GraphifyCliRunner graphify, IProjectRepository projects)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localCachePath = localCachePath;
        _repoOwner = repoOwner;
        _repoName = repoName;
        _gitSync = gitSync;
        _graphify = graphify;
        _projects = projects;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _gitSync.CloneOrPull(_repoUrl, _localCachePath, _branch);

        // --no-cluster keeps this call AST-only (no LLM): community labelling is a separate,
        // explicitly budgeted step (see the clone-time digest job) rather than folded into every
        // code ingestion, so this call stays free regardless of how often it runs.
        Directory.CreateDirectory(_localCachePath);
        await _graphify.RunAsync(_localCachePath, environment: null, ct, "extract", ".", "--no-cluster");

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
