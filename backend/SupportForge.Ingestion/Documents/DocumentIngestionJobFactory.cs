using SupportForge.Agents;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJobFactory : IIngestionJobFactory
{
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public DocumentIngestionJobFactory(ILlmClient llm, IVectorStoreService vectorStore)
    {
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project) =>
        project.KbSources
            .Where(s => s.Type == KbSourceType.Documents)
            .Select(s => new DocumentIngestionJob(project.Id, s.Location, _llm, _vectorStore));
}
