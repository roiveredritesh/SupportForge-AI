using Microsoft.Extensions.DependencyInjection;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJobFactory : IIngestionJobFactory
{
    private readonly IServiceProvider _services;

    public DocumentIngestionJobFactory(IServiceProvider services) => _services = services;

    public IEnumerable<IIngestionJob> CreateJobs(Project project)
    {
        var llm = _services.GetRequiredService<ILlmClient>();
        var vectorStore = _services.GetRequiredService<IVectorStoreService>();
        var projects = _services.GetRequiredService<IProjectRepository>();

        return project.KbSources
            .Where(s => s.Type == KbSourceType.Documents)
            .Select(s => new DocumentIngestionJob(project.Id, s.Location, llm, vectorStore, projects))
            .ToList();
    }
}
