using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;
    private readonly string _cacheRoot;

    public CodeIngestionJobFactory(IServiceProvider services, string cacheRoot)
    {
        _services = services;
        _cacheRoot = cacheRoot;
    }

    // Signature-only per U7 -- this factory never writes a TokenUsageEntry (no KbVectorIndexer
    // call), so triggeredByUserId is unused here.
    public IEnumerable<IIngestionJob> CreateJobs(Project project, string? triggeredByUserId)
    {
        var gitSync = _services.GetRequiredService<GitRepoSyncService>();
        var projects = _services.GetRequiredService<IProjectRepository>();
        var orgs = _services.GetRequiredService<IOrgRepository>();

        // One classifier instance shared across every repo of this project -- it's stateless beyond
        // its injected dependencies, so there's no reason to construct it per repo.
        var llm = _services.GetRequiredService<ILlmChatClient>();
        var contentHashes = _services.GetRequiredService<IContentHashRepository>();
        var configuration = _services.GetRequiredService<IConfiguration>();
        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var classifier = new CodeNodeClassifier(
            llm, contentHashes, ResolveConfiguredChatModel(configuration), loggerFactory.CreateLogger<CodeNodeClassifier>());

        return project.Repos.Select(r => new CodeIngestionJob(
            project.Id,
            $"https://github.com/{r.Owner}/{r.Repo}.git",
            r.DefaultBranch,
            Path.Combine(_cacheRoot, project.Id, r.Repo),
            r.Owner,
            r.Repo,
            gitSync, projects, orgs, classifier)).ToList();
    }

    // Mirrors LlmServiceCollectionExtensions' own "Llm:Provider" / "Llm:{Provider}:ChatModel"
    // resolution closely enough for cache-key purposes (R7/KTD7) -- it only needs to change when the
    // actually-configured chat model changes, not match that resolution byte-for-byte.
    private static string ResolveConfiguredChatModel(IConfiguration configuration)
    {
        var provider = configuration["Llm:Provider"];
        provider = string.IsNullOrWhiteSpace(provider) ? "OpenAI" : provider;
        return configuration[$"Llm:{provider}:ChatModel"] ?? "unknown";
    }
}
