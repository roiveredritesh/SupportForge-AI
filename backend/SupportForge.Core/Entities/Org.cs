namespace SupportForge.Core.Entities;

public sealed class Org
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    // ponytail: plaintext PAT, same trust boundary as AppUser.PasswordHash and every other
    // JSON-file-backed record here today. Not a new regression, but not encrypted at rest either.
    // Upgrade path: ISecretResolver (see ConfluenceOptions.ApiToken's U9 comment) when that lands.
    public string? GitHubAccessToken { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
