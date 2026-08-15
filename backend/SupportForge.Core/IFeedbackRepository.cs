using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IFeedbackRepository
{
    Task AddAsync(FeedbackEntry entry, CancellationToken ct = default);
    Task DeleteByProjectIdAsync(string projectId, CancellationToken ct = default);
    // U17/U18: full read of all feedback -- consumed by KbSearchTool's retrieval down-weighting
    // and FeedbackController's admin dashboard, both of which need to scan/aggregate across entries.
    Task<IReadOnlyList<FeedbackEntry>> GetAllAsync(CancellationToken ct = default);
}
