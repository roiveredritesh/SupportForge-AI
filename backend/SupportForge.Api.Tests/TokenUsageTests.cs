using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class TokenUsageTests
{
    [Fact]
    public async Task AddAsync_Then_GetTotalForProjectAsync_SumsTokensForThatProjectOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileTokenUsageRepository(tempDir);

        await repo.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow));
        await repo.AddAsync(new TokenUsageEntry("proj1", 50, DateTimeOffset.UtcNow));
        await repo.AddAsync(new TokenUsageEntry("proj2", 999, DateTimeOffset.UtcNow));

        var total = await repo.GetTotalForProjectAsync("proj1");

        Assert.Equal(150, total);
        Directory.Delete(tempDir, recursive: true);
    }
}
