namespace SupportForge.Core.Entities;

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
}
