namespace SupportForge.VectorStore.Models;

public sealed record VectorDocument(
    string Id,
    string Text,
    float[] Embedding,
    IReadOnlyDictionary<string, string> Metadata);
