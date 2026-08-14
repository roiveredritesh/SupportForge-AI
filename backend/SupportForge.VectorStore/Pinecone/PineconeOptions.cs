namespace SupportForge.VectorStore.Pinecone;

public sealed class PineconeOptions
{
    // Pinecone's data-plane host is per-index (e.g. "https://my-index-abc123.svc.us-east-1-aws.pinecone.io"),
    // obtained from the Pinecone console/control-plane after creating the index -- unlike Chroma's
    // BaseUrl, this isn't a fixed default since it's generated per index.
    public string Host { get; set; } = "";
    public string ApiKey { get; set; } = "";

    // Pinecone's score metric is higher-is-closer (cosine); null preserves the pre-existing
    // "always return topK" behavior.
    public float? MinScore { get; set; }
}
