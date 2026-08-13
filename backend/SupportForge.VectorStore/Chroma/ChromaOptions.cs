namespace SupportForge.VectorStore.Chroma;

public sealed class ChromaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string Tenant { get; set; } = "default_tenant";
    public string Database { get; set; } = "default_database";

    // Chroma's distance metric is lower-is-closer (default HNSW space is L2); null preserves the
    // pre-existing "always return topK" behavior.
    public float? MaxDistance { get; set; }
}
