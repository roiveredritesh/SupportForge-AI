using Microsoft.AspNetCore.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Identity;

// Identity storage abstraction backed by JsonFileUserRepository instead of EF Core -- see
// AppUser.cs for why. Only IUserStore/IUserPasswordStore are implemented: UserManager<AppUser>
// only needs username lookup and password-hash storage for the token-issuance flow in
// AuthController; no email/phone/lockout/claims stores exist to back, so those interfaces
// aren't implemented.
public sealed class CustomUserStore : IUserStore<AppUser>, IUserPasswordStore<AppUser>
{
    private readonly IUserRepository _repo;

    public CustomUserStore(IUserRepository repo) => _repo = repo;

    public async Task<IdentityResult> CreateAsync(AppUser user, CancellationToken cancellationToken)
    {
        await _repo.UpsertAsync(user, cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken)
    {
        await _repo.UpsertAsync(user, cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken cancellationToken)
    {
        await _repo.DeleteAsync(user.Id, cancellationToken);
        return IdentityResult.Success;
    }

    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        => _repo.GetByIdAsync(userId, cancellationToken);

    // ponytail: linear scan over GetAllAsync -- fine at the user counts this JSON-file store is
    // meant for; add an indexed lookup to IUserRepository if the user list ever gets large.
    public async Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        var all = await _repo.GetAllAsync(cancellationToken);
        return all.FirstOrDefault(u => u.NormalizedUserName == normalizedUserName);
    }

    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public Task<string> GetUserIdAsync(AppUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.UserName);

    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task SetPasswordHashAsync(AppUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(AppUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(AppUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.PasswordHash is not null);

    public void Dispose() { }
}
