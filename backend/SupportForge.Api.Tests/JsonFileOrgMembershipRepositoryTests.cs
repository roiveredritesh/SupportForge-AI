using SupportForge.Core;
using Xunit;

namespace SupportForge.Api.Tests;

public class JsonFileOrgMembershipRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileOrgMembershipRepository _repo;

    public JsonFileOrgMembershipRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _repo = new JsonFileOrgMembershipRepository(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task IsMemberAsync_NoMemberships_ReturnsFalse()
    {
        Assert.False(await _repo.IsMemberAsync("user1", "org1"));
    }

    [Fact]
    public async Task AddAsync_Then_IsMemberAsync_ReturnsTrue()
    {
        await _repo.AddAsync("user1", "org1");

        Assert.True(await _repo.IsMemberAsync("user1", "org1"));
    }

    [Fact]
    public async Task AddAsync_Twice_DoesNotDuplicate()
    {
        await _repo.AddAsync("user1", "org1");
        await _repo.AddAsync("user1", "org1");

        var orgIds = await _repo.GetOrgIdsForUserAsync("user1");

        Assert.Single(orgIds);
    }

    [Fact]
    public async Task GetOrgIdsForUserAsync_ReturnsOnlyThatUsersOrgs()
    {
        await _repo.AddAsync("user1", "org1");
        await _repo.AddAsync("user1", "org2");
        await _repo.AddAsync("user2", "org3");

        var orgIds = await _repo.GetOrgIdsForUserAsync("user1");

        Assert.Equal(new[] { "org1", "org2" }, orgIds.OrderBy(x => x));
    }

    [Fact]
    public async Task GetUserIdsForOrgAsync_ReturnsOnlyThatOrgsUsers()
    {
        await _repo.AddAsync("user1", "org1");
        await _repo.AddAsync("user2", "org1");
        await _repo.AddAsync("user3", "org2");

        var userIds = await _repo.GetUserIdsForOrgAsync("org1");

        Assert.Equal(new[] { "user1", "user2" }, userIds.OrderBy(x => x));
    }

    [Fact]
    public async Task DeleteByOrgIdAsync_RemovesAllMembershipsForOrg()
    {
        await _repo.AddAsync("user1", "org1");
        await _repo.AddAsync("user2", "org1");
        await _repo.AddAsync("user3", "org2");

        await _repo.DeleteByOrgIdAsync("org1");

        Assert.Empty(await _repo.GetUserIdsForOrgAsync("org1"));
        Assert.Single(await _repo.GetUserIdsForOrgAsync("org2"));
    }
}
