namespace SupportForge.VectorStore.Chroma;

public sealed class ChromaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string Tenant { get; set; } = "default_tenant";
    public string Database { get; set; } = "default_database";
}
