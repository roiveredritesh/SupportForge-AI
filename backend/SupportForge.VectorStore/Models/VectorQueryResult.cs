namespace SupportForge.VectorStore.Models;

public sealed record VectorQueryResult(
    string Id,
    string Text,
    float Score,
    IReadOnlyDictionary<string, string> Metadata);
