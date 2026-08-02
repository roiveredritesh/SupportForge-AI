using SupportForge.Api.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Identity;

// Covers CustomUserStore (IUserStore<AppUser>/IUserPasswordStore<AppUser>) backed by a real
// JsonFileUserRepository against a temp directory -- mirrors how ProjectsControllerTests exercises
// JsonFileProjectRepository directly.
public class JsonFileUserStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileUserRepository _repo;
    private readonly CustomUserStore _store;

    public JsonFileUserStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _repo = new JsonFileUserRepository(_tempDir);
        _store = new CustomUserStore(_repo);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static AppUser MakeUser(string id = "user-1", string userName = "alice") => new()
    {
        Id = id,
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
    };

    [Fact]
    public async Task CreateAsync_PersistsUser_RetrievableByGetAllAsync()
    {
        var user = MakeUser();

        var result = await _store.CreateAsync(user, default);

        Assert.True(result.Succeeded);
        var all = await _repo.GetAllAsync();
        Assert.Single(all, u => u.Id == user.Id);
    }

    [Fact]
    public async Task FindByIdAsync_ReturnsCreatedUser()
    {
        var user = MakeUser();
        await _store.CreateAsync(user, default);

        var found = await _store.FindByIdAsync(user.Id, default);

        Assert.NotNull(found);
        Assert.Equal(user.UserName, found!.UserName);
    }

    [Fact]
    public async Task FindByIdAsync_UnknownId_ReturnsNull()
    {
        var found = await _store.FindByIdAsync("does-not-exist", default);
        Assert.Null(found);
    }

    [Fact]
    public async Task FindByNameAsync_MatchesOnNormalizedUserName()
    {
        var user = MakeUser(userName: "Bob");
        await _store.CreateAsync(user, default);

        var found = await _store.FindByNameAsync("BOB", default);

        Assert.NotNull(found);
        Assert.Equal(user.Id, found!.Id);
    }

    [Fact]
    public async Task FindByNameAsync_UnknownName_ReturnsNull()
    {
        var found = await _store.FindByNameAsync("NOBODY", default);
        Assert.Null(found);
    }

    [Fact]
    public async Task SetAndGetPasswordHash_RoundTrips()
    {
        var user = MakeUser();

        await _store.SetPasswordHashAsync(user, "hashed-value", default);

        Assert.Equal("hashed-value", await _store.GetPasswordHashAsync(user, default));
        Assert.True(await _store.HasPasswordAsync(user, default));
    }

    [Fact]
    public async Task DeleteAsync_RemovesUser()
    {
        var user = MakeUser();
        await _store.CreateAsync(user, default);

        await _store.DeleteAsync(user, default);

        Assert.Null(await _store.FindByIdAsync(user.Id, default));
    }
}
