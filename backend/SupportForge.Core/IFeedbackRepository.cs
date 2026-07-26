using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IFeedbackRepository
{
    Task AddAsync(FeedbackEntry entry, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
}
