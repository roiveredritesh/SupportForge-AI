using Neo4j.Driver;
using SupportForge.Agents.Tools;

namespace SupportForge.Ingestion.Graph;

/// <summary>
/// U24: given a set of changed files, finds every endpoint one of them defines
/// (<c>defines_endpoint</c>, from <see cref="SupportForge.Ingestion.Code.CodeGraphExtractor"/>) and every other
/// repo in the same project whose <c>calls_endpoint</c> edge references that endpoint. Traversal is
/// scoped by <c>projectId</c> only (never <c>repo</c>) -- that's exactly what lets this bridge repos,
/// same as <see cref="GraphDbQueryTool"/>'s query pattern.
/// </summary>
public sealed class BlastRadiusQueryTool : IBlastRadiusQueryTool
{
    private readonly IDriver _driver;
    private readonly string _database;

    public BlastRadiusQueryTool(IDriver driver, string database)
    {
        _driver = driver;
        _database = database;
    }

    public async Task<IReadOnlyList<BlastRadiusEntry>> QueryAsync(
        string projectId, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
    {
        if (changedFiles.Count == 0) return [];

        await using var session = _driver.AsyncSession(o => o.WithDatabase(_database));

        var records = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (f:GraphNode {projectId: $projectId})
                WHERE f.id IN $files
                MATCH (f)-[r1:EDGE]->(ep:GraphNode {projectId: $projectId})
                WHERE r1.relation = 'defines_endpoint'
                MATCH (caller:GraphNode {projectId: $projectId})-[r2:EDGE]->(ep)
                WHERE r2.relation = 'calls_endpoint' AND caller.repo <> f.repo
                RETURN DISTINCT f.repo AS definingRepo, caller.repo AS callingRepo
                """,
                new { projectId, files = changedFiles });
            return await cursor.ToListAsync();
        });

        return records
            .Select(r => (DefiningRepo: r["definingRepo"].As<string>(), CallingRepo: r["callingRepo"].As<string>()))
            .GroupBy(r => r.DefiningRepo)
            .Select(g => new BlastRadiusEntry(g.Key, g.Select(r => r.CallingRepo).Distinct().ToList()))
            .ToList();
    }
}
