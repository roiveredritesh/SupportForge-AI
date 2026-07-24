using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJobFactory : IIngestionJobFactory
{
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly IProjectRepository _projects;

    public DocumentIngestionJobFactory(ILlmClient llm, IVectorStoreService vectorStore, IProjectRepository projects)
    {
        _llm = llm;
        _vectorStore = vectorStore;
        _projects = projects;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project) =>
        project.KbSources
            .Where(s => s.Type == KbSourceType.Documents)
            .Select(s => new DocumentIngestionJob(project.Id, s.Location, _llm, _vectorStore, _projects));
}
