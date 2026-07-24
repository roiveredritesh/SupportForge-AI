namespace SupportForge.Core.Entities;

public sealed class Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<GitHubRepoConfig> Repos { get; init; } = new();
    public List<KbSourceConfig> KbSources { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
