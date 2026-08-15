namespace SupportForge.Core.Entities;

// Sprint 1 (U4): org-wide role, one per user. L1 is least-privileged (no code-detail visibility,
// see ChatController's role gate); Admin is the org-creating self-service registrant and the only
// role that can register employees via OrgsController. Ordinal 0 (L1) is the JSON-deserialization
// default for any pre-existing user record written before this field existed.
public enum AppRole { L1, L2, L3, Admin }

// Backs the custom ASP.NET Core Identity store (SupportForge.Api/Identity/CustomUserStore.cs).
// Deliberately not IdentityUser -- this repo has no EF Core/database (see JsonFileProjectRepository
// and siblings), so the user store is JSON-file-backed like everything else and only needs the
// handful of properties IUserStore<T>/IUserPasswordStore<T> require.
public sealed class AppUser
{
    public required string Id { get; init; }
    public required string UserName { get; set; }
    public string? NormalizedUserName { get; set; }
    public string? PasswordHash { get; set; }
    public AppRole Role { get; set; } = AppRole.L1;
}
