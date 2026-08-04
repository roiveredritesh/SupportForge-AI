using System.Text.Json.Serialization;

namespace SupportForge.Ingestion.Graph;

// Shape a repo's extracted code graph is expected in on disk before GraphImportJob loads it into
// Neo4j: { nodes: [...], edges: [...] }.
public sealed class CodeGraphFile
{
    [JsonPropertyName("nodes")]
    public List<CodeGraphNode> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<CodeGraphEdge> Edges { get; set; } = [];
}

public sealed class CodeGraphNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("file_type")]
    public string FileType { get; set; } = "";

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = "";

    [JsonPropertyName("source_location")]
    public string SourceLocation { get; set; } = "";
}

public sealed class CodeGraphEdge
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "";
}
