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
    private readonly IOrgRepository _orgs;
    private readonly CodeNodeClassifier _classifier;

    public string ProjectId { get; }

    public CodeIngestionJob(string projectId, string repoUrl, string branch, string localCachePath,
        string repoOwner, string repoName, GitRepoSyncService gitSync, IProjectRepository projects,
        IOrgRepository orgs, CodeNodeClassifier classifier)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localCachePath = localCachePath;
        _repoOwner = repoOwner;
        _repoName = repoName;
        _gitSync = gitSync;
        _projects = projects;
        _orgs = orgs;
        _classifier = classifier;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Fetched up front (not just after clone, as before U15) so its OrgId is available to
        // resolve a connected GitHub MCP connection's token ahead of the clone itself.
        var project = await _projects.GetByIdAsync(ProjectId, ct);

        await _gitSync.CloneOrPullAsync(_repoUrl, _localCachePath, _branch, project?.OrgId, ct);
        Directory.CreateDirectory(_localCachePath);

        // GraphImportJobFactory reads this same path (<repo>/code-graph/graph.json) and loads it into
        // Neo4j tagged with projectId -- GraphImportJob runs as a separate, later-registered job so
        // this job doesn't need a Neo4j driver of its own.
        var graph = CodeGraphExtractor.Extract(_localCachePath);

        // OQ1 consent gate: Tier 2 classification (sends file content to the configured LLM) only
        // runs when BOTH the org-wide master switch AND this project's own toggle are explicitly
        // enabled -- neither alone is sufficient. Everything else in the pipeline (Tier 0/1
        // extraction, the graph itself) is unaffected either way.
        if (project is { CodeClassificationEnabled: true, OrgId: not null })
        {
            var org = await _orgs.GetByIdAsync(project.OrgId, ct);
            if (org is { CodeClassificationEnabled: true })
                await _classifier.ClassifyAsync(graph, _localCachePath, ProjectId, _repoName, ct);
        }

        var graphOutDir = Path.Combine(_localCachePath, "code-graph");
        Directory.CreateDirectory(graphOutDir);
        await File.WriteAllTextAsync(Path.Combine(graphOutDir, "graph.json"), JsonSerializer.Serialize(graph), ct);

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
