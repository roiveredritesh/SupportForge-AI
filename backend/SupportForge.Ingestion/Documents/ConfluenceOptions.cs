namespace SupportForge.Ingestion.Documents;

public sealed class ConfluenceOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    // ponytail: single config-bound token for now, same shape as GitRepoSyncService's GitHub:Token.
    // Per-source credential resolution (KTD7's ISecretResolver) lands with U9; this is not that yet.
    public string? ApiToken { get; set; }
}
