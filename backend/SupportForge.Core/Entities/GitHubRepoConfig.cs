namespace SupportForge.Core.Entities;

public sealed record GitHubRepoConfig(string Owner, string Repo, string DefaultBranch, string? AccessTokenSecretName);
